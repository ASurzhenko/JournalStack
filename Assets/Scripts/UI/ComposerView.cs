using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JournalStack.Client
{
    public sealed class ComposerView : MonoBehaviour
    {
        private const int WarnFrom = 1900;

        [SerializeField] private TMP_InputField _input;
        [SerializeField] private TMP_Text _counter;
        [SerializeField] private Button _sendButton;
        [SerializeField] private TMP_Text _error;

        public event Action<string> SendClicked;

        private void Awake()
        {
            _input.characterLimit = EntryRules.MaxTextLength;
            _input.onValueChanged.AddListener(OnTextChanged);
            _sendButton.onClick.AddListener(OnSend);
            OnTextChanged(_input.text);
            SetError(null);
        }

        private void OnDestroy()
        {
            _input.onValueChanged.RemoveListener(OnTextChanged);
            _sendButton.onClick.RemoveListener(OnSend);
        }

        public void SetError(string message)
        {
            _error.text = message ?? "";
            _error.gameObject.SetActive(!string.IsNullOrEmpty(message));
        }

        public void Clear()
        {
            _input.text = "";
            SetError(null);
        }

        private void OnSend()
        {
            SendClicked?.Invoke(_input.text);
        }

        private void OnTextChanged(string text)
        {
            _counter.text = text.Length + " / " + EntryRules.MaxTextLength;
            _counter.color = text.Length > WarnFrom ? new Color(1f, 0.5f, 0.5f) : new Color(0.7f, 0.7f, 0.72f);
            _sendButton.interactable = !string.IsNullOrWhiteSpace(text);
        }
    }
}
