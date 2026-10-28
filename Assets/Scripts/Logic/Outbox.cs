using System;
using System.Collections.Generic;
using UnityEngine;

namespace JournalStack.Client
{
    // Owns the local state and its file. Every change is saved at once. Record is the one place that
    // changes an entry's state after it was added.
    public sealed class Outbox
    {
        private readonly string _path;
        private readonly LocalState _state;

        public Outbox(string path)
        {
            _path = path;
            _state = StateFile.Load(path, out string warning);
            Warning = warning;
        }

        public string Warning { get; }
        public string DeviceId => _state.deviceId;
        public string Token => _state.token;
        public bool HasToken => !string.IsNullOrEmpty(_state.token);
        public IReadOnlyList<LocalEntry> Entries => _state.entries;

        public int Count(SyncState state)
        {
            int n = 0;
            foreach (LocalEntry entry in _state.entries)
            {
                if (entry.state == state)
                {
                    n++;
                }
            }

            return n;
        }

        public LocalEntry Find(string id)
        {
            return _state.entries.Find(e => e.id == id);
        }

        // The entry is written to disk before the caller touches the network. If the write fails the entry is
        // taken out again, so a later successful save cannot persist an entry the user was told failed.
        public bool AddPending(string cleanText, out string id, out string error)
        {
            var entry = new LocalEntry
            {
                id = Guid.NewGuid().ToString(),
                text = cleanText,
                clientCreatedAt = EntryRules.NewWireTime(),
                state = SyncState.Pending,
                reason = "",
            };
            _state.entries.Add(entry);
            id = entry.id;
            if (Save(out error))
            {
                return true;
            }

            _state.entries.Remove(entry);
            return false;
        }

        // Synced is final: a late failure of a duplicate request must not undo it. Failed -> Pending is Retry.
        public void Record(string id, SyncState state, string reason)
        {
            LocalEntry entry = Find(id);
            if (entry == null || entry.state == SyncState.Synced)
            {
                return;
            }

            entry.state = state;
            entry.reason = reason ?? "";
            SaveOrWarn();
        }

        // After a 401 the server does not know this device, so nothing is confirmed any more.
        public void ResetAllToPending()
        {
            foreach (LocalEntry entry in _state.entries)
            {
                entry.state = SyncState.Pending;
                entry.reason = "";
            }

            SaveOrWarn();
        }

        public void SetDevice(string deviceId, string token)
        {
            _state.deviceId = deviceId;
            _state.token = token;
            SaveOrWarn();
        }

        // A server row confirms a local entry only when id, text and time all match. Server rows with no local
        // entry are ignored. Nothing is ever removed.
        public void Merge(IEnumerable<EntryReply> serverEntries)
        {
            bool changed = false;
            foreach (EntryReply server in serverEntries)
            {
                LocalEntry local = Find(server.id);
                if (local == null)
                {
                    continue;
                }

                if (local.text == server.text && local.clientCreatedAt == server.clientCreatedAt)
                {
                    if (local.state != SyncState.Synced)
                    {
                        local.state = SyncState.Synced;
                        local.reason = "";
                        changed = true;
                    }
                }
                else if (local.state != SyncState.Failed)
                {
                    local.state = SyncState.Failed;
                    local.reason = "server_differs";
                    changed = true;
                }
            }

            if (changed)
            {
                SaveOrWarn();
            }
        }

        private bool Save(out string error)
        {
            return StateFile.Save(_path, _state, out error);
        }

        private void SaveOrWarn()
        {
            if (!Save(out string error))
            {
                Debug.LogWarning($"{nameof(Outbox)}.{nameof(SaveOrWarn)} state not saved: {error}");
            }
        }
    }
}
