using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JournalStack.Client
{
    public sealed class JournalController : MonoBehaviour
    {
        private const int TickMilliseconds = 10000;
        private const double RegisterThrottleSeconds = 60;

        [SerializeField] private ServerConfig _config;
        [SerializeField] private TMP_Text _statusText;
        [SerializeField] private Button _refreshButton;
        [SerializeField] private TMP_Text _tipText;
        [SerializeField] private ComposerView _composer;
        [SerializeField] private EntryListView _list;
        [SerializeField] private DetailView _detail;
        [SerializeField] private DeleteDataView _deleteView;

        private Outbox _outbox;
        private ApiClient _api;
        private bool _online;

        // Stale-answer guards. A generation bump (OnDestroy, or a finished delete) drops every answer that
        // arrives later; the counters keep only the newest tip/list answer; _inFlight stops two sends of one id at once.
        private int _generation;
        private bool _destroyed;
        private int _tipRequest;
        private int _listRequest;
        private readonly HashSet<string> _inFlight = new HashSet<string>();

        // A second 401 soon after a registration is final, so a refusing server cannot make a loop.
        private bool _registering;
        private DateTime _lastRegisteredUtc = DateTime.MinValue;

        // Stops two DELETE requests at once, like _registering does for registration.
        private bool _deleteSending;

        private void Start()
        {
            _ = RunSafe(StartupAsync);
        }

        private void OnDestroy()
        {
            _destroyed = true;
            _generation++;
            Application.focusChanged -= OnFocus;
            _refreshButton.onClick.RemoveListener(OnRefreshClicked);
            _composer.SendClicked -= OnSendClicked;
            _deleteView.OpenClicked -= OnDeleteOpen;
            _deleteView.DeleteConfirmed -= OnDeleteConfirmed;
        }

        private async Task RunSafe(Func<Task> work)
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                Debug.LogError($"{nameof(JournalController)}.{nameof(RunSafe)} {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task StartupAsync()
        {
            _outbox = new Outbox(Path.Combine(Application.persistentDataPath, "state.json"));
            _api = new ApiClient(_config.BaseUrl);

            _refreshButton.onClick.AddListener(OnRefreshClicked);
            _composer.SendClicked += OnSendClicked;
            _deleteView.OpenClicked += OnDeleteOpen;
            _deleteView.DeleteConfirmed += OnDeleteConfirmed;
            Application.focusChanged += OnFocus;

            if (_outbox.Warning != null)
            {
                _composer.SetError(_outbox.Warning);
            }

            Render();
            if (_outbox.DeleteRequested)
            {
                _deleteView.ShowWaiting();
            }

            // The loop lives as long as the app. Each tick takes the current generation, so a finished delete
            // (which bumps it) drops older answers without ending the loop.
            await RunSafe(() => SyncOnce(_generation));
            while (!_destroyed)
            {
                await Task.Delay(TickMilliseconds);
                if (_destroyed)
                {
                    return;
                }

                int tickGen = _generation;
                await RunSafe(() => SyncOnce(tickGen));
            }
        }

        private void OnFocus(bool focus)
        {
            if (focus)
            {
                int gen = _generation;
                _ = RunSafe(() => SyncOnce(gen));
            }
        }

        private void OnRefreshClicked()
        {
            int gen = _generation;
            _ = RunSafe(() => RefreshAll(gen));
        }

        private void OnSendClicked(string raw)
        {
            _ = RunSafe(() => SendNewAsync(raw));
        }

        private void OnRetry(string id)
        {
            _ = RunSafe(() => RetryAsync(id));
        }

        private void OnOpen(string id)
        {
            LocalEntry entry = _outbox.Find(id);
            if (entry != null)
            {
                _detail.Show(entry.text);
            }
        }

        // The tick, the focus handler and startup all run this: ping, refresh once when the server came back,
        // send what is Pending. Device endpoints are not polled; only /health is.
        private async Task SyncOnce(int gen)
        {
            bool wasOnline = _online;
            Reply ping = await _api.SendAsync("GET", "/health", null, null);
            if (gen != _generation)
            {
                return;
            }

            // Any server can answer 200, so the body has to be ours too: {"status":"ok"}.
            SetOnline(!ping.NetworkError && ping.Status == 200 && ping.Body.Contains("\"ok\""));
            if (!_online)
            {
                return;
            }

            if (_outbox.DeleteRequested)
            {
                await SendDelete(gen);
                return;
            }

            if (!wasOnline)
            {
                await RefreshAll(gen);
            }

            if (gen == _generation && _outbox.Count(SyncState.Pending) > 0)
            {
                await SendPending(gen);
            }
        }

        private async Task SendNewAsync(string raw)
        {
            int gen = _generation;
            if (_outbox.DeleteRequested)
            {
                _composer.SetError("Your data is being deleted. Write again when it is done.");
                return;
            }

            if (!EntryRules.TryClean(raw, out string text, out string error))
            {
                _composer.SetError(error);
                return;
            }

            if (!_outbox.AddPending(text, out string id, out string saveError))
            {
                _composer.SetError("Could not save the entry on this device: " + saveError);
                return;
            }

            _composer.Clear();
            Render();
            if (await TrySend(gen, id) && gen == _generation)
            {
                await RefreshList(gen);
            }
        }

        private async Task RetryAsync(string id)
        {
            int gen = _generation;
            if (_outbox.DeleteRequested)
            {
                return;
            }

            _outbox.Record(id, SyncState.Pending, "");
            Render();
            if (await TrySend(gen, id) && gen == _generation)
            {
                await RefreshList(gen);
            }
        }

        // Oldest first. Stops at the first entry that is still Pending after the attempt, so a blocked or
        // capped server costs one request per tick.
        private async Task SendPending(int gen)
        {
            bool anyStored = false;
            var ids = new List<string>();
            foreach (LocalEntry entry in _outbox.Entries)
            {
                if (entry.state == SyncState.Pending)
                {
                    ids.Add(entry.id);
                }
            }

            foreach (string id in ids)
            {
                if (_inFlight.Contains(id))
                {
                    continue;
                }

                bool stored = await TrySend(gen, id);
                if (gen != _generation)
                {
                    return;
                }

                anyStored |= stored;
                if (_outbox.Find(id).state == SyncState.Pending)
                {
                    break;
                }
            }

            if (anyStored)
            {
                await RefreshList(gen);
            }
        }

        // Returns true when the server stored the entry. The request body is built from the stored strings,
        // so a retry sends exactly the same bytes as the first try.
        private async Task<bool> TrySend(int gen, string id)
        {
            if (!_inFlight.Add(id))
            {
                return false;
            }

            try
            {
                if (_outbox.DeleteRequested)
                {
                    return false;
                }

                LocalEntry entry = _outbox.Find(id);
                if (entry == null || entry.state == SyncState.Synced)
                {
                    return false;
                }

                string body = JsonUtility.ToJson(new EntryRequest
                {
                    id = entry.id,
                    text = entry.text,
                    clientCreatedAt = entry.clientCreatedAt,
                });
                Reply reply = await _api.SendAsync("POST", "/api/v1/entries", body, _outbox.Token);
                if (gen != _generation)
                {
                    return false;
                }

                if (reply.NetworkError)
                {
                    _online = false;
                }

                switch (SendOutcome.Classify(reply, out string reason))
                {
                    case Outcome.Stored:
                        _outbox.Record(id, SyncState.Synced, "");
                        return true;
                    case Outcome.Refused:
                        _outbox.Record(id, SyncState.Failed, reason);
                        return false;
                    case Outcome.Unauthorized:
                        await HandleUnauthorized(gen, id);
                        return false;
                    default:
                        return false;
                }
            }
            finally
            {
                _inFlight.Remove(id);
                if (gen == _generation)
                {
                    Render();
                }
            }
        }

        // The server does not know this token. Register a new device, mark everything Pending (the server
        // may have lost it), and let the normal sending pick it up.
        private async Task HandleUnauthorized(int gen, string entryId)
        {
            if (_outbox.DeleteRequested || _registering)
            {
                return;
            }

            if ((DateTime.UtcNow - _lastRegisteredUtc).TotalSeconds < RegisterThrottleSeconds)
            {
                if (entryId != null)
                {
                    _outbox.Record(entryId, SyncState.Failed, "unauthorized");
                }

                return;
            }

            _registering = true;
            RegisterResult registration;
            try
            {
                registration = await _api.RegisterAsync();
            }
            finally
            {
                _registering = false;
            }

            if (gen != _generation || _outbox.DeleteRequested)
            {
                return;
            }

            if (registration.Device != null)
            {
                _outbox.SetDevice(registration.Device.deviceId, registration.Device.token);
                _lastRegisteredUtc = DateTime.UtcNow;
                _outbox.ResetAllToPending();
                Render();
                await RefreshAll(gen);
                if (gen == _generation)
                {
                    await SendPending(gen);
                }

                return;
            }

            if (registration.Reply.NetworkError)
            {
                _online = false;
            }

            Outcome outcome = SendOutcome.Classify(registration.Reply, out string reason);
            if (outcome != Outcome.NoAnswer && entryId != null)
            {
                string code = outcome == Outcome.Refused ? reason : "register_failed";
                _outbox.Record(entryId, SyncState.Failed, code);
            }
        }

        // No token and no entries: the server holds nothing for this app, so there is nothing to send.
        // A token with no entries still has a device row on the server, so the delete stays.
        private void OnDeleteOpen()
        {
            int localEntries = _outbox.Entries.Count;
            if (!_outbox.HasToken && localEntries == 0)
            {
                _deleteView.ShowResult("Nothing to delete. This app has no data on the server or on this device.");
                return;
            }

            _deleteView.ShowConfirm(localEntries == 0
                ? "Delete your data? This app's registration is removed from the server. This cannot be undone."
                : "Delete your data? Your " + localEntries
                    + " entries are removed from the server and from this device, including entries not sent yet. This cannot be undone.");
        }

        private void OnDeleteConfirmed()
        {
            _ = RunSafe(StartDeleteAsync);
        }

        private async Task StartDeleteAsync()
        {
            int gen = _generation;
            if (!_outbox.MarkDeleteRequested(out string error))
            {
                _deleteView.ShowResult("Could not save on this device: " + error + ". Nothing was sent.");
                return;
            }

            _deleteView.ShowWorking();
            await SendDelete(gen);
        }

        // Runs from the confirm and, while the flag is set, from every tick. The local file is cleared only
        // after the server confirmed, so a delete that never arrives can still be sent again.
        private async Task SendDelete(int gen)
        {
            if (_deleteSending)
            {
                return;
            }

            _deleteSending = true;
            try
            {
                Reply reply = await _api.SendAsync("DELETE", "/api/v1/devices/me", null, _outbox.Token);
                if (gen != _generation)
                {
                    return;
                }

                switch (DeleteOutcome.Classify(reply, out string reason))
                {
                    case DeleteResult.Done:
                        // Entries refused as id_taken belong to an older device on the server (D-025). This delete
                        // did not reach them, so the text has to say so.
                        int keptByOldDevice = CountTaken();
                        bool saved = _outbox.Clear(out string saveError);
                        _generation++;
                        _lastRegisteredUtc = DateTime.MinValue;
                        Render();
                        _deleteView.ShowResult(DoneText(keptByOldDevice, saved, saveError));
                        break;
                    case DeleteResult.NoAnswer:
                        if (reply.NetworkError)
                        {
                            SetOnline(false);
                        }

                        _deleteView.ShowWaiting();
                        break;
                    default:
                        _deleteView.ShowResult(_outbox.CancelDelete(out string cancelError)
                            ? "The server refused the delete (" + reason + "). Nothing was deleted."
                            : "The server refused the delete (" + reason + "). Nothing was deleted, but this device could not save that ("
                                + cancelError + ").");
                        break;
                }
            }
            finally
            {
                _deleteSending = false;
            }
        }

        private int CountTaken()
        {
            int n = 0;
            foreach (LocalEntry entry in _outbox.Entries)
            {
                if (entry.state == SyncState.Failed && entry.reason == "id_taken")
                {
                    n++;
                }
            }

            return n;
        }

        private static string DoneText(int keptByOldDevice, bool saved, string saveError)
        {
            string text = keptByOldDevice == 0
                ? "Your entries were deleted from the server and from this device."
                : "Deleted from this device and from the server, except " + keptByOldDevice
                    + " entries the server keeps under an older registration of this app. The admin can remove them.";
            if (!saved)
            {
                text += " This device could not save the change (" + saveError + "). It finishes on the next start.";
            }

            return text;
        }

        private async Task RefreshAll(int gen)
        {
            await RefreshTip(gen);
            if (gen == _generation)
            {
                await RefreshList(gen);
            }
        }

        private async Task RefreshTip(int gen)
        {
            int request = ++_tipRequest;
            Reply reply = await _api.SendAsync("GET", "/api/v1/tip/today", null, _outbox.Token);
            if (gen != _generation || request != _tipRequest)
            {
                return;
            }

            if (reply.NetworkError)
            {
                SetOnline(false);
            }
            else if (reply.Status == 401)
            {
                await HandleUnauthorized(gen, null);
            }
            else if (reply.Status == 204)
            {
                _tipText.text = "No tip yet.";
            }
            else if (reply.Status == 200 && Json.TryParse(reply.Body, "text", out TipReply tip))
            {
                _tipText.text = tip.text;
            }
            else
            {
                Debug.LogWarning($"{nameof(JournalController)}.{nameof(RefreshTip)} unusable answer, status {reply.Status}; the card is left as it was");
            }
        }

        private async Task RefreshList(int gen)
        {
            int request = ++_listRequest;
            Reply reply = await _api.SendAsync("GET", "/api/v1/entries", null, _outbox.Token);
            if (gen != _generation || request != _listRequest)
            {
                return;
            }

            if (reply.NetworkError)
            {
                SetOnline(false);
            }
            else if (reply.Status == 401)
            {
                await HandleUnauthorized(gen, null);
            }
            else if (reply.Status == 200 && Json.TryParse(reply.Body, "entries", out EntriesReply entries))
            {
                _outbox.Merge(entries.entries);
                Render();
            }
            else
            {
                Debug.LogWarning($"{nameof(JournalController)}.{nameof(RefreshList)} unusable answer, status {reply.Status}; the list is left as it was");
            }
        }

        private void SetOnline(bool online)
        {
            _online = online;
            UpdateStatus();
        }

        private void Render()
        {
            _list.Render(_outbox.Entries, OnOpen, OnRetry);
            UpdateStatus();
        }

        // "Online · 1 waiting · 2 failed": Failed rows are counted so they cannot sit unseen.
        private void UpdateStatus()
        {
            string line = _online ? "Online" : "Offline";
            int pending = _outbox.Count(SyncState.Pending);
            int failed = _outbox.Count(SyncState.Failed);
            if (pending > 0)
            {
                line += " · " + pending + " waiting";
            }

            if (failed > 0)
            {
                line += " · " + failed + " failed";
            }

            _statusText.text = line;
        }
    }
}
