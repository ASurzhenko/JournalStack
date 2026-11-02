using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JournalStack.Client
{
    // The "Delete my data" button and its panel. It holds no logic: the controller decides what to show.
    // This component sits on an object that stays active; only the panel is hidden, so the open button keeps working.
    public sealed class DeleteDataView : MonoBehaviour
    {
        [SerializeField] private Button _openButton;
        [SerializeField] private GameObject _panel;
        [SerializeField] private TMP_Text _message;
        [SerializeField] private Button _confirmButton;
        [SerializeField] private Button _closeButton;

        public event Action OpenClicked;
        public event Action DeleteConfirmed;

        private void Awake()
        {
            _openButton.onClick.AddListener(OnOpenClicked);
            _confirmButton.onClick.AddListener(OnConfirmClicked);
            _closeButton.onClick.AddListener(Hide);
            _panel.SetActive(false);
        }

        private void OnDestroy()
        {
            _openButton.onClick.RemoveListener(OnOpenClicked);
            _confirmButton.onClick.RemoveListener(OnConfirmClicked);
            _closeButton.onClick.RemoveListener(Hide);
        }

        // The count is of local entries, so the person sees what goes.
        public void ShowConfirm(int localEntries)
        {
            Show("Delete your data? Your " + localEntries
                + " entries are removed from the server and from this device, including entries not sent yet. This cannot be undone.",
                true, true);
            _closeButton.GetComponentInChildren<TMP_Text>().text = "Cancel";
        }

        public void ShowWorking()
        {
            Show("Deleting...", false, false);
        }

        // No Close button: once the request may have reached the server there is no way back.
        public void ShowWaiting()
        {
            Show("The delete is not confirmed yet. The app keeps trying every 10 seconds and when it starts. New entries wait until it is done.",
                false, false);
        }

        public void ShowResult(string text)
        {
            Show(text, false, true);
            _closeButton.GetComponentInChildren<TMP_Text>().text = "Close";
        }

        private void Show(string text, bool confirm, bool close)
        {
            _message.text = text;
            _confirmButton.gameObject.SetActive(confirm);
            _closeButton.gameObject.SetActive(close);
            _panel.SetActive(true);
        }

        private void Hide()
        {
            _panel.SetActive(false);
        }

        private void OnOpenClicked()
        {
            OpenClicked?.Invoke();
        }

        private void OnConfirmClicked()
        {
            DeleteConfirmed?.Invoke();
        }
    }
}
