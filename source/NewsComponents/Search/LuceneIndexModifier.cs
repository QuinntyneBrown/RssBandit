﻿
//#define TRACE_INDEX_OPS

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using log4net;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Store;
using NewsComponents.Collections;
using NewsComponents.Utils;
using RssBandit.Common.Logging;
using Directory=System.IO.Directory;


#pragma warning disable CS0618 // Type or member is obsolete

namespace NewsComponents.Search
{

	#region IndexOperation enum

	/// <summary>
	/// This is an enum that describes the set of operations that can be placed in the 
	/// queue of operations to perform on the search index by the index modifying thread. 
	/// </summary>
	internal enum IndexOperation:byte {
		AddSingleDocument = 10, // == queue priority!
		AddMultipleDocuments = 11,	
		// ReIndex operation is a delete(docs) first followed by add(docs)
		// so Delete must have the highest priority for batched index 
		// operations:
		DeleteDocuments = 50, 
		// delete of feeds should follow add/remove docs, or we waste
		// the index with old non existing feed item docs if there is still
		// a pending add/delete docs in the queue.
		DeleteFeed = 2,
		OptimizeIndex = 1,
	}

	#endregion

	#region PendingIndexOperation class

	/// <summary>
	/// This is a class that is used to represent a pending operation on the index in 
	/// that is currently in the pending operation queue. 
	/// </summary>
	internal class PendingIndexOperation
	{
		public IndexOperation Action;
		public object[] Parameters;

		/// <summary>
		/// Constructor 
		/// </summary>
		/// <param name="action">The operation to perform on the index</param>
		/// <param name="parameters">The parameters to the operation</param>
		public PendingIndexOperation(IndexOperation action, object[] parameters){
			this.Action = action;
			this.Parameters = parameters; 
		}
	}

	internal delegate void FinishedIndexOperationEventHandler(object sender, FinishedIndexOperationEventArgs e);
	internal class FinishedIndexOperationEventArgs: EventArgs
	{
		public readonly PendingIndexOperation Operation;
		public FinishedIndexOperationEventArgs(PendingIndexOperation op) {
			this.Operation = op;
		}
	}

	#endregion
	
	/// <summary>
	/// Serialize the index modifications (only one index modifier,
	/// IndexReader or IndexWriter can change the index at the same directory
	/// the same time).
	/// </summary>
	internal class LuceneIndexModifier: IDisposable 
	{
		#region fields

		/// <summary>
		/// To be used to synchronize index modifications. Only one
		/// IndexWriter/Reader can modify the index at a time!
		/// </summary>
		public object SyncRoot = new Object();

        /// <summary>
        /// Used to synchronize access to the 'open' boolean flag. 
        /// </summary>
        public object OpenLock = new Object();

		public event EventHandler<FinishedIndexOperationEventArgs> FinishedIndexOperation;

		private readonly LuceneSettings settings;
		private Lucene.Net.Store.Directory indexBaseDirectory;
		private bool open, flushInprogress, threadRunning;
		
		// (initially not signalled)
		private AutoResetEvent startProcessPendingOpsSignal;
		private RegisteredWaitHandle rwhProcessPendingOps;

		private readonly PriorityQueue pendingIndexOperations = new PriorityQueue(); 

		// logging/tracing:
		private static readonly ILog _log = Log.GetLogger(typeof(LuceneIndexModifier));	
		private static readonly LuceneInfoWriter _logHelper = new LuceneInfoWriter(_log); 

		protected internal IndexWriter indexWriter;
		//protected internal IndexReader indexReader = null;

		private const int TimeToDelayBeforeRetry = 1000; /* 1 second */ 

        /// <summary>
        /// This is the maximum number of segments that can exist at any time. 
        /// </summary>
        /// <remarks>This corresponds to the MergeFactor of the index. </remarks>
        private const int MaxSegments = 20;

        /// <summary>
        /// Number of documents that are buffered in memory before they are written to disk
        /// to create a single segment. 
        /// </summary>
        /// <remarks>Assumption is that RSS feeds with 50 items are around 100K so this should 
        /// be the average size of a segment. </remarks>
        private const int DocsPerSegment = 50; 

		#endregion

		#region Constructors

		public LuceneIndexModifier(LuceneSettings settings)
		{
			this.settings = settings;
			
			this.indexBaseDirectory = settings.GetIndexDirectory();
			this.Init();

			StartIndexerThread();
		}

		#endregion

		#region public properties/methods (general)
		
		/// <summary>
		/// Gets or sets the base directory for the index.
		/// </summary>
		/// <value>The base directory.</value>
		public Lucene.Net.Store.Directory BaseDirectory
		{
			get { return indexBaseDirectory; }
			set { indexBaseDirectory = value; }
		}

		/// <summary>
		/// Gets true if an Index exists.
		/// </summary>
		/// <returns></returns>
		public bool IndexExists 
		{
			get { return IndexReader.IndexExists(this.BaseDirectory); }
		}

		/// <summary> 
		/// Make sure all changes are written to disk (pending operations
		/// and index).
		/// </summary>
		// <exception cref="IOException" ></exception>
		public virtual void Flush(bool close)
		{
			// force flush all:
            try
            {
                //BUGBUG: We don't index every document in this case. 
                if (close)
					FlushPendingOperations( Math.Min(pendingIndexOperations.Count, 10 /* Int32.MaxValue - locks up the UI on shutdown */));
                FlushIndex(close);
            }
            catch (Exception e)
            {
                _log.Error("Error on flushing index and pending operations", e); 
            }
		}

		/// <summary>
		/// Resets the pending operations (clear) and
		/// reset the index (re-create new) as one operation.
		/// </summary>
		public virtual void Reset() 
		{
			ResetPendingOperations();
			ResetIndex();
		}
		
		/// <summary>
		/// Stops the indexer (thread),
		/// performs all pending operations on the index and 
		/// flushes all pending I/O writes to disk. 
		/// </summary>
		public void StopIndexer() 
		{
			this.StopIndexerThread();
			
			// wait for batched indexing tasks:
			while (this.flushInprogress)
				Thread.Sleep(50);
			
			this.Flush(true);
		}
		
		/// <summary>
		/// Returns a <see cref="T:System.String"/> that represents the current <see cref="T:System.Object"/>.
		/// </summary>
		/// <returns>
		/// A <see cref="T:System.String"/> that represents the current <see cref="T:System.Object"/>.
		/// </returns>
		public override string ToString()
		{
			return this.settings.ToString();
		}

		#endregion

		#region public methods (PendingIndexOperation related)
		
		/// <summary> Adds a document to this index, using the provided culture. 
		/// If the document contains more than
		/// {@link #SetMaxFieldLength(int)} terms for a given field, the remainder are
		/// discarded.
		/// </summary>
		/// <remarks>This operation is added to the pending index operations queue.</remarks>
		/// <exception cref="InvalidOperationException">If the index is closed </exception>
		public virtual void  Add(Document doc, string culture) 
		{
			if (IsIndexerRunning) lock (this.pendingIndexOperations.SyncRoot)
				{
				this.pendingIndexOperations.Enqueue((int)IndexOperation.AddSingleDocument,
					new PendingIndexOperation(IndexOperation.AddSingleDocument, new object[]{doc, culture}));
				startProcessPendingOpsSignal.Set();
			}
		}

		/// <summary> Adds a document to this index, using the provided culture. 
		/// If the document contains more than
		/// {@link #SetMaxFieldLength(int)} terms for a given field, the remainder are
		/// discarded.
		/// </summary>
		/// <exception cref="InvalidOperationException">If the index is closed </exception>
		public virtual void AddRange(Document[] docs, string culture)
		{
			if (IsIndexerRunning) lock (this.pendingIndexOperations.SyncRoot)
			{
				this.pendingIndexOperations.Enqueue((int)IndexOperation.AddMultipleDocuments,
					new PendingIndexOperation(IndexOperation.AddMultipleDocuments, new object[] { docs, culture }));
				startProcessPendingOpsSignal.Set();
			}
		}

		/// <summary> Deletes all documents containing <code>term</code>.
		/// This is useful if one uses a document field to hold a unique ID string for
		/// the document.  Then to delete such a document, one merely constructs a
		/// term with the appropriate field and the unique ID string as its text and
		/// passes it to this method.  Returns the number of documents deleted.
		/// </summary>
		/// <returns> the number of documents deleted
		/// </returns>
		/// <seealso cref="IndexReader.DeleteDocuments(Term)">
		/// </seealso>
		/// <exception cref="InvalidOperationException">If the index is closed </exception>
		public virtual void Delete(Term term)
		{
			if (IsIndexerRunning) lock (this.pendingIndexOperations.SyncRoot)
			{
				this.pendingIndexOperations.Enqueue((int)IndexOperation.DeleteDocuments,
					new PendingIndexOperation(IndexOperation.DeleteDocuments, new object[] { term }));
				startProcessPendingOpsSignal.Set();
			}
		}
		
		/// <summary>
		/// Deletes the feed from the index. Same as Delete(Term),
		/// but with a lower priority.
		/// </summary>
		/// <param name="term">The term.</param>
		public virtual void DeleteFeed(Term term)
		{
			if (IsIndexerRunning) lock (this.pendingIndexOperations.SyncRoot)
			{
				// differs only in the priority, the operation is the same:
				this.pendingIndexOperations.Enqueue((int)IndexOperation.DeleteFeed,
					new PendingIndexOperation(IndexOperation.DeleteDocuments, new object[] { term }));
				startProcessPendingOpsSignal.Set();
			}
		}
		
		/// <summary> Merges all segments together into a single segment, optimizing an index
		/// for search.
		/// </summary>
		/// <seealso cref="IndexWriter.Optimize()">
		/// </seealso>
		/// <exception cref="InvalidOperationException">If the index is closed </exception>
		public virtual void Optimize()
		{
			if (IsIndexerRunning) lock (this.pendingIndexOperations.SyncRoot)
			{
				this.pendingIndexOperations.Enqueue((int)IndexOperation.OptimizeIndex,
					new PendingIndexOperation(IndexOperation.OptimizeIndex, null));
				startProcessPendingOpsSignal.Set();
			}
		}

		#endregion

		#region public methods (Index related)
		
		/// <summary>
		/// Creates the index.
		/// </summary>
		public void CreateIndex() {
			IndexWriter writer = new IndexWriter(this.settings.GetIndexDirectory(), new StandardAnalyzer(), true);
			writer.Close();
		}

		/// <summary> Returns the number of documents currently in this index.</summary>
		/// <seealso cref="IndexWriter.DocCount()">
		/// </seealso>
		/// <seealso cref="IndexReader.NumDocs()">
		/// </seealso>
		/// <exception cref="InvalidOperationException">If the index is closed </exception>
		public virtual int NumberOfDocuments()
		{
			lock (SyncRoot)
			{
				AssureOpen();
				if (indexWriter != null)
				{
					return indexWriter.DocCount();
				}
				return 0;
			}
		}

		/// <summary>
		/// Deletes the whole index, then creates a new one 
		/// without any documents.
		/// </summary>
		public virtual void ResetIndex()
		{
            lock (OpenLock) {
                this.Close();
                if (this.BaseDirectory is RAMDirectory) {
                    // nothing to do...?
                } else if (this.BaseDirectory is FSDirectory &&
                    settings.IndexPath != null) {
                    Directory.Delete(settings.IndexPath, true);
                    Directory.CreateDirectory(settings.IndexPath);
                } else {
                    Debug.Assert(false, "Unhandled BaseDirectory type: " + this.BaseDirectory.GetType().FullName);
                }
            }//lock

            //this is outside the lock() to avoid deadlock
                this.BaseDirectory = settings.GetIndexDirectory(true);
                this.Init();
            
		}

		/// <summary> 
		/// Close this index, writing all pending changes to disk.
		/// </summary>
		public virtual void Close()
		{
			lock (this.SyncRoot)
			{
				if (!open) return;
				if (indexWriter != null)
				{
					try { indexWriter.Close(); } 
					catch (Exception closeEx) { _log.Error("Failed to close indexWriter", closeEx);}
					indexWriter = null;
				}
				open = false;
			}
		}

		#endregion

		#region private methods (IndexThread related)

		private bool IsIndexerRunning
		{
			get { return rwhProcessPendingOps != null && startProcessPendingOpsSignal != null; }
		}

		private void StartIndexerThread () 
		{
			this.threadRunning = true;
			// (initially not signalled)
			startProcessPendingOpsSignal = new AutoResetEvent(false);
			
			// Tell the thread pool to wait on the AutoResetEvent.
			rwhProcessPendingOps = ThreadPool.RegisterWaitForSingleObject(
				startProcessPendingOpsSignal, ThreadRun, null,
				Timeout.Infinite, false);
		}
		
		private void StopIndexerThread()
		{
			threadRunning = false;
			// Tell the thread pool to stop waiting on the event:
			if (rwhProcessPendingOps != null)
			{
				rwhProcessPendingOps.Unregister(null);
				rwhProcessPendingOps = null;
			}
			if (startProcessPendingOpsSignal != null)
			{
				startProcessPendingOpsSignal.Close();
				startProcessPendingOpsSignal = null;
			}
		}

		/// <summary>
		/// This thread loops if it gets the wakeup signal by the startProcessPendingOpsSignal
		/// AutoResetEvent popping items from the pendingIndexOperations queue
		/// and performing the actions. This ensures that there is only one thread
		/// modifying the index at any given time. 
		/// </summary>
		private void ThreadRun(object ignored, bool timedOut)
		{
			// runs if timedOut, or signalled, but stop on threadRunning flag:
			if (threadRunning)
			{
				if (false == this.flushInprogress &&
					this.pendingIndexOperations.Count > 0)
				{
					do
					{
						// do not calc percentage on a few items:
						FlushPendingOperations(Math.Max(200, this.pendingIndexOperations.Count / 10));
						if (threadRunning)
							Thread.Sleep(1000 * 5); //sleep 5 secs
					} 
					while (threadRunning && this.pendingIndexOperations.Count > 0);
				}

			}//if(threadRunning)
		}

		

		#endregion

		#region private methods (PendingIndexOperation related)

		/// <summary>
		/// Performs the specified PendingIndexOperation.
		/// </summary>
		/// <param name="current">The operation to perform</param>
		private void PerformOperation(PendingIndexOperation current){
		
			try { 

				switch(current.Action){
					
					case IndexOperation.AddSingleDocument:
						this.AddSingleDocument((Document)current.Parameters[0], (string)current.Parameters[1]);
						break;

					case IndexOperation.AddMultipleDocuments:
						this.AddMultipleDocuments((Document[])current.Parameters[0], (string)current.Parameters[1]);
						break; 

					case IndexOperation.DeleteDocuments:
						this.DeleteTerm((Term)current.Parameters[0]);
						break;

					case IndexOperation.OptimizeIndex:
						this.OptimizeIndex();
						break;
					default:
						Debug.Assert(false, "Unknown index operation: " + current.Action);
						return;
				}

			}catch(FileNotFoundException fe){ 
				/* index has gotten corrupted and refers to a non-existence index file */
				this.ResetIndex(); 
				_log.Error("Index is corrupted, recreating index:", fe);				
			}catch(IndexOutOfRangeException ioore){
				/* index has gotten corrupted, */ 
				this.ResetIndex(); 
				_log.Error("Index is corrupted, recreating index:", ioore);
            } catch (UnauthorizedAccessException uae) {
                /* another process may be accessing index files */
                _log.Error("Index files may be in use, sleeping:", uae);
                Thread.Sleep(TimeToDelayBeforeRetry);
            }catch (ArgumentOutOfRangeException aoore)
            {
                /* index has gotten corrupted, */
                this.ResetIndex();
                _log.Error("Index is corrupted, recreating index:", aoore);
            }
			
			RaiseFinishedIndexOperationEvent(current);
		}

		private void FlushPendingOperations(int batchedItemsAmount) 
		{
			try {
				this.flushInprogress = true;
				
				do {
					PendingIndexOperation pendingOp = null;

					//perform all queued operations on the index
					lock (this.pendingIndexOperations.SyncRoot) {
						if (this.pendingIndexOperations.Count > 0) {
							pendingOp = this.pendingIndexOperations.Dequeue() as PendingIndexOperation;
						}
					} //lock 

					//Optimizing the index is an expensive operation so we don't want to 
					//call it if the queue is being flushed since it may delay application exit. 
					if ((pendingOp != null) && (pendingOp.Action != IndexOperation.OptimizeIndex)) {
						this.PerformOperation(pendingOp);
					}

					batchedItemsAmount--;

					//potential race condition on this.pendingIndexOperations.Count but chances are very low
				} while (this.pendingIndexOperations.Count > 0 && batchedItemsAmount >= 0);
			
			} finally {
				this.flushInprogress = false;
			}
		}

		private void ResetPendingOperations() 
		{
			lock(this.pendingIndexOperations.SyncRoot){
				this.pendingIndexOperations.Clear();
			}
		}

		private void RaiseFinishedIndexOperationEvent(PendingIndexOperation current) {
			if (this.FinishedIndexOperation != null)
				this.FinishedIndexOperation(this, new FinishedIndexOperationEventArgs(current));
		}
		
		#endregion

		#region private methods (Index modification related)

		/// <summary> Adds a document to this index, using the provided culture. 
		/// If the document contains more than
		/// {@link #SetMaxFieldLength(int)} terms for a given field, the remainder are
		/// discarded.
		/// </summary>
		/// <exception cref="InvalidOperationException">If the index is closed </exception>
		private void AddSingleDocument(Document doc, string culture)
		{
			if (doc == null) return;
//#if TRACE_INDEX_OPS
			_log.DebugFormat("Adding document {0} to the index",  doc.GetField(LuceneSearch.Keyword.ItemLink));
//#endif
			lock (SyncRoot)
			{
				AssureOpen();
				CreateIndexWriter();
                try
                {
                    if (!string.IsNullOrEmpty(culture))
                        indexWriter.AddDocument(doc, LuceneSearch.GetAnalyzer(culture));
                    else
                        indexWriter.AddDocument(doc);
                }
                catch (IOException ioe)
                {
                    _log.Error("IOException adding document to the index", ioe);

                    /* see  http://issues.apache.org/jira/browse/LUCENE-665 */
                    if (ioe.Message.IndexOf("segments.new") != -1)
                    {
                        FileHelper.MoveFile(Path.Combine(this.settings.IndexPath, "segments.new"), Path.Combine(this.settings.IndexPath, "segments"), MoveFileFlag.ReplaceExisting);
                    }
                    else if (ioe.Message.IndexOf("deleteable.new") != -1)
                    {
                        FileHelper.MoveFile(Path.Combine(this.settings.IndexPath, "deleteable.new"), Path.Combine(this.settings.IndexPath, "deleteable"), MoveFileFlag.ReplaceExisting);
                    }
                }
                catch (UnauthorizedAccessException uae)
                {
                    _log.Error("Access denied error while adding document to the index", uae);

                    /* see  http://issues.apache.org/jira/browse/LUCENE-665 */
                    if (uae.Message.IndexOf("segments.new") != -1)
                    {
                        FileHelper.MoveFile(Path.Combine(this.settings.IndexPath, "segments.new"), Path.Combine(this.settings.IndexPath, "segments"), MoveFileFlag.ReplaceExisting);
                    }
                    else if (uae.Message.IndexOf("deleteable.new") != -1)
                    {
                        FileHelper.MoveFile(Path.Combine(this.settings.IndexPath, "deleteable.new"), Path.Combine(this.settings.IndexPath, "deleteable"), MoveFileFlag.ReplaceExisting);
                    }
                }
                catch (SystemException se) //indicates "docs out of order" when trying to merge docs in index
                {
                    _log.Error("Non-fatal error occured while adding document to index", se); 
                }

			}

		}

		/// <summary> Adds a document to this index, using the provided culture. 
		/// If the document contains more than
		/// {@link #SetMaxFieldLength(int)} terms for a given field, the remainder are
		/// discarded.
		/// </summary>
		/// <exception cref="InvalidOperationException">If the index is closed </exception>
		private void AddMultipleDocuments(Document[] docs, string culture)
		{
			if (docs == null || docs.Length == 0) return;

			Analyzer analyzer = null;
			if (!string.IsNullOrEmpty(culture))
				analyzer = LuceneSearch.GetAnalyzer(culture);
#if TRACE_INDEX_OPS
			_log.Info("Add multiple IndexDoc(s)...");
#endif			
			lock (SyncRoot) 
			{
				AssureOpen();
				CreateIndexWriter();
				
				for (int i = 0; i < docs.Length; i++)
					if (analyzer != null)
						indexWriter.AddDocument(docs[i], analyzer);
					else
						indexWriter.AddDocument(docs[i]);
			}
		}

		/// <summary> Deletes all documents containing <code>term</code>.
		/// This is useful if one uses a document field to hold a unique ID string for
		/// the document.  Then to delete such a document, one merely constructs a
		/// term with the appropriate field and the unique ID string as its text and
		/// passes it to this method.  Returns the number of documents deleted.
		/// </summary>
		/// <returns> the number of documents deleted
		/// </returns>
		/// <exception cref="InvalidOperationException">If the index is closed </exception>
		private void DeleteTerm(Term term)
		{
//#if TRACE_INDEX_OPS
			_log.DebugFormat("Deleting documents that match '{0}' from the index", term.ToString());
//#endif			
			lock (SyncRoot)
			{
				AssureOpen();
				CreateIndexWriter();
                try
                {
                    indexWriter.DeleteDocuments(term);
                }
                catch(IOException ioe)
                {
                    _log.Error("IOException deleting document from the index", ioe);
                }
			}
		}

		private void FlushIndex(bool closeWriterOnly) 
		{
#if TRACE_INDEX_OPS
			_log.Info("FlushIndex...");
#endif
            lock (SyncRoot)
            {
                AssureOpen();
                if (indexWriter != null)
                {
                    try
                    {

                        indexWriter.Close();
                        indexWriter = null;
                        if (!closeWriterOnly)
                            CreateIndexWriter();
                    }
                    catch (FileNotFoundException fe)
                    {
                        /* index has gotten corrupted and refers to a non-existence index file */
                        this.ResetIndex();
                        _log.Error("Index is corrupted, recreating index:", fe);
                    }
                    catch (IndexOutOfRangeException ioore)
                    {
                        /* index has gotten corrupted, */
                        this.ResetIndex();
                        _log.Error("Index is corrupted, recreating index:", ioore);
                    }
                    catch (UnauthorizedAccessException uae)
                    {
                        /* another process may be accessing index files */
                        _log.Error("Index files may be in use, sleeping:", uae);
                        Thread.Sleep(TimeToDelayBeforeRetry);
                    }
                }//if(indexWriter != null)
            }//lock
		}

		/// <summary> Merges all segments together into a single segment, optimizing an index
		/// for search.
		/// </summary>
		/// <seealso cref="IndexWriter.Optimize()">
		/// </seealso>
		/// <exception cref="InvalidOperationException">If the index is closed </exception>
		private void OptimizeIndex()
		{
#if TRACE_INDEX_OPS
			_log.Info("OptimizeIndex...");
#endif
			//since this significantly modifies the index, we don't want other operations 
			//occuring at the same time
			lock (SyncRoot)
			{
				AssureOpen();
				CreateIndexWriter();
				indexWriter.Optimize();
			}
		}

		#endregion

		#region private methods (general)

		/// <summary> Initialize an IndexWriter.</summary>
		/// <exception cref="IOException"></exception>
		protected internal void Init() 
		{
			lock (this.SyncRoot) {
				this.indexWriter =new IndexWriter(this.settings.GetIndexDirectory(),
					LuceneSearch.GetAnalyzer(LuceneSearch.DefaultLanguage), !this.IndexExists);
				open = true;
			}
		}
		
		/// <summary> Throw an IllegalStateException if the index is closed.</summary>
		/// <exception cref="InvalidOperationException"> If index is closed</exception>
		protected internal virtual void AssureOpen() {
            lock (OpenLock) { //we might be reseting the index
                if (!open) {
                    throw new InvalidOperationException("Index is closed");
                }
            }
		}

		/// <summary> Close the IndexReader and open an IndexWriter.</summary>
		/// <exception cref="IOException"></exception>
		protected internal virtual void  CreateIndexWriter() 
		{
			if (this.indexWriter == null) 
			{
#if TRACE_INDEX_OPS
				_log.Info("Creating IndexWriter...");
#endif
				this.indexWriter = new IndexWriter(this.BaseDirectory, 
					LuceneSearch.GetAnalyzer(LuceneSearch.DefaultLanguage), false);
				this.indexWriter.SetInfoStream(_logHelper);
                this.indexWriter.SetMergeFactor(MaxSegments);
                this.indexWriter.SetMaxBufferedDocs(DocsPerSegment);
				this.indexWriter.SetMergeScheduler(new NoExceptionsConcurrentMergeScheduler());
			}
		}

		#endregion

		#region IDisposable Members

		/// <summary>
		/// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
		/// </summary>
		public void Dispose() 
		{
			StopIndexerThread();
			Close();

			GC.SuppressFinalize(this);
		}

		#endregion
	}

	/*
	 * according to http://mail-archives.apache.org/mod_mbox/lucene-java-user/200902.mbox/%3c39D2833E-C1BE-425A-B7E8-47C17748E792@mikemccandless.com%3e 
	 * we create our own exception ignoring MergeScheduler class 
	 */
	internal class NoExceptionsConcurrentMergeScheduler: ConcurrentMergeScheduler
	{
		public NoExceptionsConcurrentMergeScheduler()
		{
            // as long there is no  handleMergeException method to overwrite,
            // we use this one:
            this.SetSuppressExceptions();//.SetSuppressExceptions_ForNUnitTest();
		} 
	}

	/**
	 * Helper class which writes internal Lucene debug info to RSS Bandit trace logs. 
	 */
	internal class LuceneInfoWriter: StreamWriter{

		private readonly ILog logger; 
		
		/// <summary>
		/// We don't want a default constructor
		/// </summary>
		private LuceneInfoWriter(): base(new MemoryStream()){;}


		/// <summary>
		/// 
		/// </summary>
		public override System.Text.Encoding Encoding {
			get {
				return null;
			}
		}


		/// <summary>
		/// Constructor accepts logger as input
		/// </summary>
		/// <param name="logger">The logger to which we'll actually write the information</param>
		internal LuceneInfoWriter(ILog logger) : base(new MemoryStream()){
			this.logger = logger; 					
		}

		public override void Write(string value) {
			logger.Debug(value); 			
		}

		public override void Write(string format, params object[] args) {
			logger.DebugFormat(format, args); 		
		}
		
	}
}

#pragma warning restore CS0618 // Type or member is obsolete
