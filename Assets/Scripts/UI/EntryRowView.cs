using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JournalStack.Client
{
    public sealed class EntryRowView : MonoBehaviour
    {
        private const int PreviewChars = 80;

        [SerializeField] private TMP_Text _time;
        [SerializeField] private TMP_Text _preview;
        [SerializeField] private TMP_Text _state;
        [SerializeField] private TMP_Text _reason;
        [SerializeField] private Button _bodyButton;
        [SerializeField] private Button _retryButton;

        private string _id;
        private Action<string> _onOpen;
        private Action<string> _onRetry;

        private void Awake()
        {
            _bodyButton.onClick.AddListener(OnBody);
            _retryButton.onClick.AddListener(OnRetry);
        }

        private void OnDestroy()
        {
            _bodyButton.onClick.RemoveListener(OnBody);
            _retryButton.onClick.RemoveListener(OnRetry);
        }

        public void Bind(LocalEntry entry, Action<string> onOpen, Action<string> onRetry)
        {
            _id = entry.id;
            _onOpen = onOpen;
            _onRetry = onRetry;

            _time.text = LocalTime(entry.clientCreatedAt);
            _preview.text = Preview(entry.text);
            _reason.text = entry.reason ?? "";
            _reason.gameObject.SetActive(entry.state == SyncState.Failed && !string.IsNullOrEmpty(entry.reason));
            _retryButton.interactable = entry.state == SyncState.Failed;

            switch (entry.state)
            {
                case SyncState.Synced:
                    _state.text = "Synced";
                    _state.color = new Color(0.45f, 0.85f, 0.55f);
                    break;
                case SyncState.Failed:
                    _state.text = "Failed · tap to retry";
                    _state.color = new Color(1f, 0.5f, 0.5f);
                    break;
                default:
                    _state.text = "Pending";
                    _state.color = new Color(1f, 0.78f, 0.35f);
                    break;
            }
        }

        // Several lines become one, and a long entry is cut on purpose: the detail view shows all of it.
        public static string Preview(string text)
        {
            string flat = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            return flat.Length <= PreviewChars ? flat : flat.Substring(0, PreviewChars) + "...";
        }

        private static string LocalTime(string wire)
        {
            if (DateTime.TryParseExact(wire, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime utc))
            {
                return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }

            return wire;
        }

        private void OnBody()
        {
            _onOpen?.Invoke(_id);
        }

        private void OnRetry()
        {
            _onRetry?.Invoke(_id);
        }
    }
}
