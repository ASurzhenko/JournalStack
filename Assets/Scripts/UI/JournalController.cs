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

        private Outbox _outbox;
        private ApiClient _api;
        private bool _online;

        // Stale-answer guards. A generation bump (OnDestroy) drops every answer that arrives later; the
        // counters keep only the newest tip/list answer; _inFlight stops two sends of one id at once.
        private int _generation;
        private int _tipRequest;
        private int _listRequest;
        private readonly HashSet<string> _inFlight = new HashSet<string>();

        // A second 401 soon after a registration is final, so a refusing server cannot make a loop.
        private bool _registering;
        private DateTime _lastRegisteredUtc = DateTime.MinValue;

        private void Start()
        {
            _ = RunSafe(StartupAsync);
        }

        private void OnDestroy()
        {
            _generation++;
            Application.focusChanged -= OnFocus;
            _refreshButton.onClick.RemoveListener(OnRefreshClicked);
            _composer.SendClicked -= OnSendClicked;
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
            int gen = _generation;
            _outbox = new Outbox(Path.Combine(Application.persistentDataPath, "state.json"));
            _api = new ApiClient(_config.BaseUrl);

            _refreshButton.onClick.AddListener(OnRefreshClicked);
            _composer.SendClicked += OnSendClicked;
            Application.focusChanged += OnFocus;

            if (_outbox.Warning != null)
            {
                _composer.SetError(_outbox.Warning);
            }

            Render();
            await RunSafe(() => SyncOnce(gen));
            while (gen == _generation)
            {
                await Task.Delay(TickMilliseconds);
                if (gen != _generation)
                {
                    return;
                }

                await RunSafe(() => SyncOnce(gen));
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
            if (_registering)
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

            if (gen != _generation)
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
