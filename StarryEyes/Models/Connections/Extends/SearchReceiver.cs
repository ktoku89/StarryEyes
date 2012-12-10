﻿using StarryEyes.Breezy.Api.Rest;
using StarryEyes.Models.Hubs;
using StarryEyes.Models.Stores;
using System.Collections.Generic;
using System.Linq;

namespace StarryEyes.Models.Connections.Extends
{
    public sealed class SearchReceiver : PollingConnectionBase
    {
        private static object searchLocker = new object();
        private static SortedDictionary<string, SearchReceiver> searchReceiverResolver
            = new SortedDictionary<string, SearchReceiver>();
        private static SortedDictionary<string, int> searchReferenceCount
            = new SortedDictionary<string, int>();

        public static void RegisterSearchQuery(string query)
        {
            lock (searchLocker)
            {
                if (searchReferenceCount.ContainsKey(query))
                {
                    searchReferenceCount[query]++;
                    return;
                }
                else
                {
                    searchReferenceCount.Add(query, 1);
                    var receiver = new SearchReceiver(query);
                    receiver.IsActivated = true;
                    searchReceiverResolver.Add(query, receiver);
                }
            }
        }

        public static void RemoveSearchQuery(string query)
        {
            lock (searchLocker)
            {
                if (!searchReferenceCount.ContainsKey(query))
                    return;
                if (--searchReferenceCount[query] == 0)
                {
                    searchReferenceCount.Remove(query);
                    var receiver = searchReceiverResolver[query];
                    searchReceiverResolver.Remove(query);
                    receiver.Dispose();
                }
            }
        }

        private string _query;
        private SearchReceiver(string query)
            : base(null)
        {
            this._query = query;
        }

        protected override int IntervalSec
        {
            get { return 60; }
        }

        protected override void DoReceive()
        {
            SearchReceiver.DoReceive(_query);
        }

        public static void DoReceive(string query, long? max_id = null)
        {
            var authInfo = AccountsStore.Accounts.Shuffle().Select(s => s.AuthenticateInfo).FirstOrDefault();
            if (authInfo == null)
            {
                AppInformationHub.PublishInformation(new AppInformation(AppInformationKind.Warning,
                    "SEARCH_RECEIVE_ACCOUNT_NOT_FOUND",
                    "アカウントが存在しないため、検索タイムラインを受信できません。",
                    "アカウントを一つ以上登録してください。"));
                return;
            }
            authInfo.SearchTweets(query, max_id: max_id)
                .RegisterToStore();
        }
    }
}