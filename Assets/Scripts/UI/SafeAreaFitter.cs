using UnityEngine;

namespace JournalStack.Client
{
    /// <summary>
    /// Converts <see cref="Screen.safeArea"/> into anchors on this RectTransform. Re-applied when the
    /// resolution, the orientation or the safe area itself changes — four field reads per frame and no
    /// allocation, because there is no reliable event for any of the three.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public sealed class SafeAreaFitter : MonoBehaviour
    {
        private RectTransform _rect;
        private Rect _lastSafeArea;
        private int _lastWidth;
        private int _lastHeight;
        private ScreenOrientation _lastOrientation;

        private void Awake()
        {
            _rect = (RectTransform)transform;
        }

        private void OnEnable()
        {
            Apply();
        }

        private void Update()
        {
            if (Screen.safeArea == _lastSafeArea
                && Screen.width == _lastWidth
                && Screen.height == _lastHeight
                && Screen.orientation == _lastOrientation)
            {
                return;
            }

            Apply();
        }

        private void Apply()
        {
            _lastSafeArea = Screen.safeArea;
            _lastWidth = Screen.width;
            _lastHeight = Screen.height;
            _lastOrientation = Screen.orientation;

            if (_lastWidth <= 0 || _lastHeight <= 0)
            {
                return;
            }

            Vector2 min = _lastSafeArea.position;
            Vector2 max = min + _lastSafeArea.size;

            min.x /= _lastWidth;
            min.y /= _lastHeight;
            max.x /= _lastWidth;
            max.y /= _lastHeight;

            _rect.anchorMin = min;
            _rect.anchorMax = max;
            _rect.offsetMin = Vector2.zero;
            _rect.offsetMax = Vector2.zero;
        }
    }
}
