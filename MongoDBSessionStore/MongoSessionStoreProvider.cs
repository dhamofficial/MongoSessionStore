﻿using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Collections.Specialized;
using System.Web;
using System.Web.Configuration;
using System.Configuration;
using System.Configuration.Provider;
using System.Web.SessionState;
using MongoDB.Driver;
using MongoDB.Driver.Configuration;


namespace MongoDBSessionStore
{
    public sealed class MongoSessionStoreProvider : SessionStateStoreProviderBase
    {
        private SessionStateSection sessionStateSection = null; 
        private string eventSource = "MongoSessionStore";
        private string eventLog = "Application";

        private bool _logExceptions = false;
        public bool WriteExceptionsToEventLog
        {
            get { return _logExceptions; }
            set { _logExceptions = value; }
        }

        //
        // The ApplicationName property is used to differentiate sessions
        // in the data source by application.
        //
        public string _applicationName;
        public string ApplicationName
        {
            get { return _applicationName; }
        }

        public override void Initialize(string name, NameValueCollection config)
        {
            if (config == null)
                throw new ArgumentNullException("config");

            if (name == null || name.Length == 0)
                name = "MongoSessionStore";

            if (String.IsNullOrEmpty(config["description"]))
            {
                config.Remove("description");
                config.Add("description", "MongoDB Session State Store provider");
            }
            // Initialize the abstract base class.
            base.Initialize(name, config);

            _applicationName = System.Web.Hosting.HostingEnvironment.ApplicationVirtualPath;

            Configuration cfg = WebConfigurationManager.OpenWebConfiguration(ApplicationName);
            sessionStateSection = (SessionStateSection)cfg.GetSection("system.web/sessionState");
            if (config["writeExceptionsToEventLog"] != null)
            {
                if (config["writeExceptionsToEventLog"].ToUpper() == "TRUE")
                    _logExceptions = true;
            }
        }

        public override void Dispose()
        {
        }

        public override bool SetItemExpireCallback(SessionStateItemExpireCallback expireCallback)
        {
            return false;
        }

        public override void SetAndReleaseItemExclusive(HttpContext context, string id, SessionStateStoreData item, object lockId, bool newItem)
        {
            string sessionItems = Serialize((SessionStateItemCollection)item.Items);

            try
            {
                if (newItem)
                {
                    // Delete an existing expired session if it exists.
                    SessionStore.EvictExpiredSession(id, _applicationName);

                    // insert new session item.
                    Session session = new Session(id, this._applicationName, item.Timeout, sessionItems, item.Items.Count,0);
                    SessionStore.Insert(session);
                }
                else
                {
                    SessionStore.UpdateSession(id, DateTime.Now.AddMinutes((Double)item.Timeout), sessionItems, this._applicationName, item.Items.Count, lockId);
                }
            }
            catch (Exception e)
            {
                if (WriteExceptionsToEventLog)
                {
                    WriteToEventLog(e, "SetAndReleaseItemExclusive");
                    throw new ProviderException(e.Message, e.InnerException);
                }
                else
                    throw e;
            }
        }


        public override SessionStateStoreData GetItem(HttpContext context,
          string id,
          out bool locked,
          out TimeSpan lockAge,
          out object lockId,
          out SessionStateActions actionFlags)
        {
            return GetSessionStoreItem(false, context, id, out locked, out lockAge, out lockId, out actionFlags);
        }

        public override SessionStateStoreData GetItemExclusive(HttpContext context,
          string id,
          out bool locked,
          out TimeSpan lockAge,
          out object lockId,
          out SessionStateActions actionFlags)
        {
            return GetSessionStoreItem(true, context, id, out locked, out lockAge, out lockId, out actionFlags);
        }


        //
        // GetSessionStoreItem is called by both the GetItem and 
        // GetItemExclusive methods. GetSessionStoreItem retrieves the 
        // session data from the data source. If the lockRecord parameter
        // is true (in the case of GetItemExclusive), then GetSessionStoreItem
        // locks the record and sets a new LockId and LockDate.
        //
        private SessionStateStoreData GetSessionStoreItem(bool lockRecord, 
            HttpContext context, 
            string id, 
            out bool locked, 
            out TimeSpan lockAge,
            out object lockId, 
            out SessionStateActions actionFlags)
        {
            // Initial values for return value and out parameters.
            SessionStateStoreData item = null;
            lockAge = TimeSpan.Zero;
            lockId = null;
            locked = false;
            actionFlags = 0;

            // String to hold serialized SessionStateItemCollection.
            string serializedItems = "";
            // Timeout value from the data store.
            int timeout = 0;

            try
            {
                Session session = SessionStore.Get(id, this._applicationName);    
                // lockRecord is true when called from GetItemExclusive and
                // false when called from GetItem.
                // Obtain a lock if possible. Evict the record if it is expired.
                if (session == null)
                {
                    // Not found. The locked value is false.
                    locked = false;
                }
                else if(session.Expires < DateTime.Now)
                {
                    locked = false;
                    SessionStore.EvictSession(session);

                }
                else if (session.Locked)
                {
                    locked = true;
                    lockAge = DateTime.Now.Subtract(session.LockDate);
                    lockId = session.LockID;
                }
                else
                {
                    locked = false;
                    lockId = session.LockID;
                    actionFlags = (SessionStateActions)session.Flags;
                    //timeout = reader.GetInt32(5);

                    if (lockRecord)
                    {
                        lockId = (int)lockId + 1;
                        session.LockID = lockId;
                        session.Flags = 0;  
                        SessionStore.LockSession(session);
                    }

                    if (actionFlags == SessionStateActions.InitializeItem)
                        item = CreateNewStoreData(context, sessionStateSection.Timeout.Minutes);
                    else
                        item = Deserialize(context, serializedItems, timeout);
                }            
                          
            }
            catch (Exception e)
            {
                if (WriteExceptionsToEventLog)
                {
                    WriteToEventLog(e, "GetSessionStoreItem");
                    throw new ProviderException(e.Message,e.InnerException);
                }
                else
                    throw e;
            }
            return item;
        }

        //
        // Serialize is called by the SetAndReleaseItemExclusive method to 
        // convert the SessionStateItemCollection into a Base64 string to    
        // be stored in an Access Memo field.
        //

        private string Serialize(SessionStateItemCollection items)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter writer = new BinaryWriter(ms);

            if (items != null)
                items.Serialize(writer);

            writer.Close();

            return Convert.ToBase64String(ms.ToArray());
        }

        //
        // DeSerialize is called by the GetSessionStoreItem method to 
        // convert the Base64 string stored in the Access Memo field to a 
        // SessionStateItemCollection.
        //

        private SessionStateStoreData Deserialize(HttpContext context, string serializedItems, int timeout)
        {
            MemoryStream ms =
              new MemoryStream(Convert.FromBase64String(serializedItems));

            SessionStateItemCollection sessionItems =
              new SessionStateItemCollection();

            if (ms.Length > 0)
            {
                BinaryReader reader = new BinaryReader(ms);
                sessionItems = SessionStateItemCollection.Deserialize(reader);
            }

            return new SessionStateStoreData(sessionItems,
              SessionStateUtility.GetSessionStaticObjects(context),
              timeout);
        }

        //
        // SessionStateProviderBase.ReleaseItemExclusive
        //
        public override void ReleaseItemExclusive(HttpContext context, string id, object lockId)
        {
            try
            {
                SessionStore.ReleaseLock(id, this._applicationName, lockId);
            }
            catch (Exception e)
            {
                if (WriteExceptionsToEventLog)
                {
                    WriteToEventLog(e, "ReleaseItemExclusive");
                    throw new ProviderException(e.Message,e.InnerException);
                }
                else
                    throw e;
            }

        }


        //
        // SessionStateProviderBase.RemoveItem
        //

        public override void RemoveItem(HttpContext context, string id, object lockId, SessionStateStoreData item)
        {
            try
            {
                SessionStore.EvictSession(id, this._applicationName, lockId);
            }
            catch (Exception e)
            {
                if (WriteExceptionsToEventLog)
                {
                    WriteToEventLog(e, "RemoveItem");
                    throw new ProviderException(e.Message,e.InnerException);
                }
                else
                    throw e;
            }     
        }

        public override void CreateUninitializedItem(HttpContext context,string id,int timeout)
        {
            Session session = new Session(id,this._applicationName, timeout, String.Empty, 0, SessionStateActions.InitializeItem);

            try
            {
                SessionStore.Insert(session);
            }
            catch (Exception e)
            {
                if (WriteExceptionsToEventLog)
                {
                    WriteToEventLog(e, "CreateUninitializedItem");
                    throw new ProviderException(e.Message,e.InnerException);
                }
                else
                    throw e;
            }
        }

        public override SessionStateStoreData CreateNewStoreData(HttpContext context,int timeout)
        {
            return new SessionStateStoreData(new SessionStateItemCollection(),SessionStateUtility.GetSessionStaticObjects(context),timeout);
        }

        public override void ResetItemTimeout(HttpContext context, string id)
        {
            try
            {
                SessionStore.UpdateSessionExpiration(id, DateTime.Now.AddMinutes(sessionStateSection.Timeout.TotalMinutes));
            }
            catch (Exception e)
            {
                if (WriteExceptionsToEventLog)
                {
                    WriteToEventLog(e, "ResetItemTimeout");
                    throw new ProviderException(e.Message,e.InnerException);
                }
                else
                    throw e;
            }
        }

        public override void InitializeRequest(HttpContext context)
        {
        }


        public override void EndRequest(HttpContext context)
        {
        }

        private void WriteToEventLog(Exception e, string action)
        {
            EventLog log = new EventLog();
            log.Source = eventSource;
            log.Log = eventLog;

            string message =
              "An exception occurred ";
            message += "Action: " + action + "\n\n";
            message += "Exception: " + e.ToString();
            log.WriteEntry(message);
        }
    }
}