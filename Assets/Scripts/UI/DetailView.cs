using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JournalStack.Client
{
    // Full text of one entry in a scroll view, so a long entry is readable to its last line.
    public sealed class DetailView : MonoBehaviour
    {
        [SerializeField] private TMP_Text _text;
        [SerializeField] private RectTransform _content;
        [SerializeField] private ScrollRect _scroll;
        [SerializeField] private Button _closeButton;

        private void Awake()
        {
            _closeButton.onClick.AddListener(Hide);
            gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            _closeButton.onClick.RemoveListener(Hide);
        }

        public void Show(string text)
        {
            _text.text = text;
            gameObject.SetActive(true);
            _text.ForceMeshUpdate();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            _scroll.verticalNormalizedPosition = 1f;
        }

        public void Hide()
        {
            gameObject.SetActive(false);
        }
    }
}
