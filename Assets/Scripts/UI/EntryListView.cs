using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace JournalStack.Client
{
    public sealed class EntryListView : MonoBehaviour
    {
        [SerializeField] private RectTransform _content;
        [SerializeField] private EntryRowView _rowPrefab;

        // Newest first. The list is reversed before the stable sort so that two entries written in the
        // same second keep the later one on top.
        public void Render(IReadOnlyList<LocalEntry> entries, Action<string> onOpen, Action<string> onRetry)
        {
            for (int i = _content.childCount - 1; i >= 0; i--)
            {
                // Destroy waits for the end of the frame; hiding the row now keeps it out of the layout rebuild below.
                GameObject old = _content.GetChild(i).gameObject;
                old.SetActive(false);
                Destroy(old);
            }

            foreach (LocalEntry entry in entries.Reverse().OrderByDescending(e => e.clientCreatedAt, StringComparer.Ordinal))
            {
                Instantiate(_rowPrefab, _content).Bind(entry, onOpen, onRetry);
            }

            // Text set in code only marks the layout dirty; rebuild now so rows do not overlap for a frame.
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
        }
    }
}
