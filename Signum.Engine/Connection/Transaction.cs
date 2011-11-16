﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Data.SqlClient;
using System.Data;
using Signum.Utilities.DataStructures;
using Signum.Utilities;
using System.Diagnostics;
using System.IO;
using Signum.Engine.Properties;
using Signum.Entities;

namespace Signum.Engine
{
    /// <summary>
    /// Allows easy nesting of transaction using 'using' statement
    /// Keeps an implicit stack of Transaction objects over the StackTrace of the current Thread
    /// and an explicit stack of RealTransaction objects on thread variable.
    /// Usually, just the first Transaccion creates a RealTransaction, but you can create more using 
    /// forceNew = true
    /// All Transaction can cancel but only the one that created the RealTransaction can Commit 
    /// </summary>
    public class Transaction : IDisposable
    {
        public static Action<string, StackTrace> UnexpectedBehaviourCallback { get; set; }

        static Transaction()
        {
            UnexpectedBehaviourCallback = (msg, st) => Debug.WriteLine(msg); 
        }

        static void NotifyRollback()
        {
            UnexpectedBehaviourCallback("TRANSACTION ROLLBACKED!", new StackTrace(2, true));
        }

        static readonly IVariable<Dictionary<BaseConnection, ICoreTransaction>> currents = Statics.ThreadVariable<Dictionary<BaseConnection, ICoreTransaction>>("transactions");

        bool commited;
        ICoreTransaction coreTransaction; 

        interface ICoreTransaction
        {
            event Action RealCommit;
            event Action PreRealCommit;
            SqlConnection Connection { get; }
            SqlTransaction Transaction { get; }
            DateTime Time { get; }
            bool RolledBack { get; }
            bool Started { get; }
            void Rollback();
            void Commit();
            ICoreTransaction Finish();
            void Start();

            Dictionary<string, object> UserData { get; }
        }
     
        class FakedTransaction : ICoreTransaction
        {
            ICoreTransaction parent;

            public FakedTransaction(ICoreTransaction parent)
            {
                this.parent = parent;
            }

            public event Action RealCommit
            {
                add { parent.RealCommit += value; }
                remove { parent.RealCommit -= value; }
            }

            public event Action PreRealCommit
            {
                add { parent.PreRealCommit += value; }
                remove { parent.PreRealCommit -= value; }
            }

            public SqlConnection Connection{ get { return parent.Connection; } }
            public SqlTransaction Transaction{ get { return parent.Transaction; } }
            public DateTime Time{ get { return parent.Time;} }
            public bool RolledBack { get{ return parent.RolledBack;} }
            public bool Started { get { return parent.Started; } }

            public void Start() { parent.Start(); }

            public void Rollback()
            {
                parent.Rollback(); 
            }

            public void Commit(){ }

            public ICoreTransaction Finish() { return parent; }

            public Dictionary<string, object> UserData
            {
                get { return parent.UserData; }
            }
        }

        class RealTransaction : ICoreTransaction
        {
            ICoreTransaction parent; 

            public SqlConnection Connection { get; private set; }
            public SqlTransaction Transaction { get; private set; }
            public DateTime Time { get; private set; }
            public bool RolledBack { get; private set; }
            public bool Started { get; private set; }
            public event Action RealCommit;
            public event Action PreRealCommit;

            IsolationLevel? IsolationLevel;

            public RealTransaction(ICoreTransaction parent, IsolationLevel? isolationLevel)
            {
                IsolationLevel = isolationLevel;
                Time = TimeZoneManager.Now;
                this.parent = parent;
            }

            public void Start()
            {
                if (!Started)
                {
                    Connection con = (Connection)ConnectionScope.Current;
                    Connection = new SqlConnection(con.ConnectionString);
                    
                    Connection.Open();
                    Transaction = Connection.BeginTransaction(IsolationLevel ?? con.IsolationLevel);
                    Started = true;
                }
            }

            public void Commit()
            {
                if (Started)
                {
                    while (PreRealCommit != null)
                    {
                        foreach (Action item in PreRealCommit.GetInvocationList())
                        {
                            item();
                            PreRealCommit -= item;
                        }
                    }

                    Transaction.Commit();

                    while (RealCommit != null)
                    {
                        foreach (Action item in RealCommit.GetInvocationList())
                        {
                            item();
                            RealCommit -= item;
                        }
                    }
                }
            }

            public void Rollback()
            {
                if (Started && !RolledBack)
                {
                    Transaction.Rollback();
                    NotifyRollback();
                    RolledBack = true;
                }
            }

            public ICoreTransaction Finish()
            {
                if (Transaction != null)
                {
                    Transaction.Dispose();
                    Transaction = null;
                }

                if (Connection != null)
                {
                    Connection.Dispose();
                    Connection = null;
                }

                return parent;
            }

            Dictionary<string, object> userData;
            public Dictionary<string, object> UserData
            {
                get { return userData ?? (userData = new Dictionary<string, object>());  }
            }
        }

        class NamedTransaction : ICoreTransaction
        {
            ICoreTransaction parent;
            string savePointName;
            public bool RolledBack { get; private set; }
            public bool Started { get; private set; }
            public event Action RealCommit;
            public event Action PreRealCommit;

            public NamedTransaction(ICoreTransaction parent, string savePointName)
            {
                this.parent = parent;
                this.savePointName = savePointName;
            }

        
            public SqlConnection Connection { get { return parent.Connection; } }
            public SqlTransaction Transaction { get { return parent.Transaction; } }
            public DateTime Time { get { return parent.Time; } }

            public void Start()
            {
                if (!Started)
                {
                    parent.Start();
                    Transaction.Save(savePointName);
                    Started = true;
                }
            }

            public void Rollback()
            {
                if (Started && !RolledBack)
                {
                    Transaction.Rollback(savePointName);
                    NotifyRollback();
                    RolledBack = true;
                }
            }

            public void Commit() 
            {
                while (PreRealCommit != null)
                {
                    foreach (Action item in PreRealCommit.GetInvocationList())
                    {
                        item();
                        PreRealCommit -= item;
                    }
                }

                while (RealCommit != null)
                {
                    foreach (Action item in RealCommit.GetInvocationList())
                    {
                        item();
                        RealCommit -= item;
                    }
                }
            }

            public ICoreTransaction Finish() { return parent; }

            public Dictionary<string, object> UserData
            {
                get { return parent.UserData; }
            }
        }

        class NoneTransaction : ICoreTransaction
        {
            ICoreTransaction parent;

            public SqlConnection Connection { get; private set; }
            public SqlTransaction Transaction { get{return null;}}
            public DateTime Time { get; private set; }
            public bool RolledBack { get; private set; }
            public bool Started { get; private set; }
            public event Action RealCommit;
            public event Action PreRealCommit;

            public NoneTransaction(ICoreTransaction parent)
            {
                Time = TimeZoneManager.Now;
                this.parent = parent;
            }

            public void Start()
            {
                if (!Started)
                {
                    Connection con = (Connection)ConnectionScope.Current;
                    Connection = new SqlConnection(con.ConnectionString);

                    Connection.Open();
                    //Transaction = Connection.BeginTransaction(IsolationLevel ?? con.IsolationLevel);
                    Started = true;
                }
            }

            public void Commit()
            {
                if (Started)
                {
                    while (PreRealCommit != null)
                    {
                        foreach (Action item in PreRealCommit.GetInvocationList())
                        {
                            item();
                            PreRealCommit -= item;
                        }
                    }

                    //Transaction.Commit();

                    while (RealCommit != null)
                    {
                        foreach (Action item in RealCommit.GetInvocationList())
                        {
                            item();
                            RealCommit -= item;
                        }
                    }
                }
            }

            public void Rollback()
            {
                if (Started && !RolledBack)
                {
                    //Transaction.Rollback();
                    NotifyRollback();
                    RolledBack = true;
                }
            }

            public ICoreTransaction Finish()
            {
                //if (Transaction != null)
                //{
                //    Transaction.Dispose();
                //    Transaction = null;
                //}

                if (Connection != null)
                {
                    Connection.Dispose();
                    Connection = null;
                }

                return parent;
            }

            Dictionary<string, object> userData;
            public Dictionary<string, object> UserData
            {
                get { return userData ?? (userData = new Dictionary<string, object>()); }
            }
        }

        public Transaction():this(false){}

        public Transaction(bool forceNew)
        {
            BaseConnection bc = AssertConnection();

            var dic = currents.Value;
            ICoreTransaction parent = dic.TryGetC(bc);
            if (parent == null || forceNew)
            {
                dic[bc] = coreTransaction = new RealTransaction(parent, null);
            }
            else
            {
                AssertTransaction();
                dic[bc] = coreTransaction = new FakedTransaction(parent);
            }
        }
    
        Transaction(BaseConnection bc, ICoreTransaction transaction)
        {
            currents.Value[bc] = coreTransaction = transaction;
        }

        public static Transaction None()
        {
            BaseConnection bc = AssertConnection();

            ICoreTransaction parent = currents.Value.TryGetC(bc);

            return new Transaction(bc, new NoneTransaction(parent));
        }

        public static Transaction NamedSavePoint(string savePointName)
        {
            BaseConnection bc = AssertConnection();

            ICoreTransaction parent = GetCurrent();
            return new Transaction(bc, new NamedTransaction(parent, savePointName));
        }

        public static Transaction ForceNew()
        {
            return ForceNew(null);
        }

        public static Transaction ForceNew(IsolationLevel? isolationLevel)
        {
            BaseConnection bc = AssertConnection();

            ICoreTransaction parent = currents.Value.TryGetC(bc);

            return new Transaction(bc, new RealTransaction(parent, isolationLevel));
        }

        private static BaseConnection AssertConnection()
        {
            if (currents.Value == null)
                currents.Value = new Dictionary<BaseConnection, ICoreTransaction>();

            BaseConnection bc = ConnectionScope.Current;

            if (bc == null)
                throw new InvalidOperationException("ConnectionScope.Current not established. Use ConnectionScope.Default to do it.");
            return bc;
        }

        
        void AssertTransaction()
        {
            if (GetCurrent().RolledBack)
                throw new InvalidOperationException("The transation is RolledBack");
        }

        static ICoreTransaction GetCurrent()
        {
            return currents.Value.GetOrThrow(ConnectionScope.Current, "No Transaction created yet");
        }

        public static event Action RealCommit
        {
            add { GetCurrent().RealCommit += value; }
            remove { GetCurrent().RealCommit -= value; }
        }

        public static event Action PreRealCommit
        {
            add { GetCurrent().PreRealCommit += value; }
            remove { GetCurrent().PreRealCommit -= value; }
        }

        public static Dictionary<string, object> UserData
        {
            get { return GetCurrent().UserData; }
        }

        public static bool HasTransaction
        {
            get { return currents != null && currents.Value.ContainsKey(ConnectionScope.Current); }
        }

        public static SqlConnection CurrentConnection
        {
            get
            {
                ICoreTransaction tran = GetCurrent();
                tran.Start();
                return tran.Connection;
            }
        }

        public static SqlTransaction CurrentTransaccion
        {
            get
            {
                ICoreTransaction tran = GetCurrent();
                tran.Start();
                return tran.Transaction;
            }
        }


        public static DateTime StartTime
        {
            get
            {
                ICoreTransaction tran = GetCurrent();
                return tran.Time;
            }
        }

        public T Commit<T>(T returnValue)
        {
            Commit();
            return returnValue;
        }

        public void Commit()
        {
            AssertTransaction();

            coreTransaction.Commit();

            commited = true;
        }

        public void Dispose()
        {
            if (!commited)
                coreTransaction.Rollback();

            ICoreTransaction parent = coreTransaction.Finish();

            if (parent == null)
                currents.Value.Remove(ConnectionScope.Current);
            else
                currents.Value[ConnectionScope.Current] = parent;
        }

        public static void ForceClean()
        {
            if (currents != null && currents.Value.Count != 0)
            {
                UnexpectedBehaviourCallback("DIRTY TRANSACTIONS FOUND!", new StackTrace(1));
                currents.Value.Clear();
            }
        }
    }
}
