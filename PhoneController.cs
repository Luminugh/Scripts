using System;
using System.Collections.Generic;
using UnityEngine;
using FinDeMes.Management;

namespace FinDeMes.Management.Phone
{
    /// <summary>Authoritative owner of phone open/closed state. Hosts the GTA-like launcher and switches to fullscreen apps.
    /// Acquires input lock and pause when open.</summary>
    public sealed class PhoneController : MonoBehaviour
    {
        public PhoneConfig Config;
        public PlayerInputLockService InputLock;
        public GameClockService Clock;
        public NotificationService Notifications;
        public EconomyReadModel EconomyReadModel;
        public EventJournal Journal;
        public PhoneAppRegistry Registry;
        public PhoneBadgeService BadgeService;

        public bool IsOpen { get; private set; }
        public bool HasFullscreenApp => currentHost != null;
        public PhoneAppId CurrentApp => currentHost != null ? currentHost.AppId : PhoneAppId.Messages;

        GameObject canvasRoot;
        GameObject eventSystem;
        GameObject phoneRoot;
        GameObject launcherRoot;
        GameObject fullscreenRoot;
        GameObject notificationLayer;
        GameObject phoneDeviceRoot;
        GameObject screenDimRoot;
        Vector2 deviceHomeAnchoredPosition;
        bool deviceHomePositionCaptured;
        PhoneLauncherPresenter launcher;
        FullscreenAppHost currentHost;
        readonly Stack<PhoneAppId> navigationStack = new();
        IDisposable inputLockToken;
        IDisposable pauseToken;
        NotificationPopupPresenter notificationPresenter;
        Coroutine transition;
        RectTransform deviceRect;
        CanvasGroup deviceCanvasGroup;
        CursorLockMode cursorLockBeforeOpen;
        bool cursorVisibilityBeforeOpen;

        public event System.Action<bool> OpenStateChanged;
        public event System.Action<PhoneAppId> AppOpened;
        public event System.Action PhoneClosed;

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;
            if (InputLock != null && inputLockToken == null)
                inputLockToken = InputLock.Acquire("Phone");
            if (Config != null && Config.PauseSimulationOnOpen && Clock != null && pauseToken == null)
                pauseToken = Clock.AcquirePause(PauseReason.Phone, "PhoneController");

            cursorLockBeforeOpen = Cursor.lockState;
            cursorVisibilityBeforeOpen = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (transition != null) { StopCoroutine(transition); transition = null; }
            if (deviceRect != null && deviceHomePositionCaptured)
                deviceRect.anchoredPosition = deviceHomeAnchoredPosition;
            if (deviceCanvasGroup != null)
                deviceCanvasGroup.alpha = 0f;
            if (canvasRoot != null) canvasRoot.SetActive(true);
            ShowLauncher();
            OpenStateChanged?.Invoke(true);
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            if (inputLockToken != null) { inputLockToken.Dispose(); inputLockToken = null; }
            if (pauseToken != null) { pauseToken.Dispose(); pauseToken = null; }

            Cursor.lockState = cursorLockBeforeOpen;
            Cursor.visible = cursorVisibilityBeforeOpen;

            DestroyCurrentHost();
            navigationStack.Clear();
            if (transition != null) StopCoroutine(transition);
            if (canvasRoot != null && deviceCanvasGroup != null)
            {
                if (Application.isPlaying) transition = StartCoroutine(AnimateClose());
                else canvasRoot.SetActive(false);
            }
            else if (canvasRoot != null)
                canvasRoot.SetActive(false);
            OpenStateChanged?.Invoke(false);
            PhoneClosed?.Invoke();
        }

        public void Toggle()
        {
            if (IsOpen) Close(); else Open();
        }

        public void OpenApp(PhoneAppId appId)
        {
            if (Registry == null || !Registry.Contains(appId)) return;
            var definition = Registry.Get(appId);
            if (definition.IsLocked && appId != PhoneAppId.Map && appId != PhoneAppId.Delivery) return;
            navigationStack.Push(appId);
            ShowFullscreenApp(appId);
            AppOpened?.Invoke(appId);
        }

        /// <summary>Backwards-compatible alias for OpenApp. Used by legacy Polleria presenter registration.</summary>
        public void NavigateTo(PhoneAppId appId) => OpenApp(appId);

        public void GoBack()
        {
            if (currentHost == null) { Close(); return; }
            if (navigationStack.Count > 1)
            {
                navigationStack.Pop();
                ShowFullscreenApp(navigationStack.Peek());
            }
            else
            {
                GoHome();
            }
        }

        public void GoHome()
        {
            DestroyCurrentHost();
            navigationStack.Clear();
            ShowLauncher();
        }

        void ShowLauncher()
        {
            DestroyCurrentHost();
            if (phoneDeviceRoot != null) phoneDeviceRoot.SetActive(true);
            if (screenDimRoot != null) screenDimRoot.SetActive(false);
            if (launcher == null) return;
            if (launcherRoot == null)
            {
                launcherRoot = launcher.Build(phoneRoot.transform);
                launcher.AppSelected += HandleAppSelected;
                launcher.CloseRequested += Close;
            }
            else
            {
                launcherRoot.SetActive(true);
            }
            launcher.Refresh();
            AnimateDevice(true);
        }

        void ShowFullscreenApp(PhoneAppId appId)
        {
            if (Registry == null) return;
            if (!Registry.TryGet(appId, out var def)) return;
            if (def.IsLocked && appId != PhoneAppId.Map && appId != PhoneAppId.Delivery) return;

            DestroyCurrentHost();
            if (launcherRoot != null) launcherRoot.SetActive(false);

            var presenter = def.PresenterFactory();
            if (phoneDeviceRoot != null) phoneDeviceRoot.SetActive(false);
            if (screenDimRoot != null) screenDimRoot.SetActive(true);
            currentHost = new FullscreenAppHost(appId, def.Title, presenter);
            currentHost.Build(canvasRoot.transform);
            fullscreenRoot = currentHost.Root;
            if (Application.isPlaying) StartCoroutine(AnimateFullscreenIn(fullscreenRoot));
            currentHost.OnBack += GoBack;
            currentHost.OnHome += GoHome;
            currentHost.OnClose += Close;
            currentHost.Refresh();
        }

        void DestroyCurrentHost()
        {
            if (currentHost != null)
            {
                currentHost.Destroy();
                currentHost = null;
            }
            fullscreenRoot = null;
        }

        void HandleAppSelected(PhoneAppId id)
        {
            OpenApp(id);
        }

        public void RegisterApp(PhoneAppDefinition definition)
        {
            if (Registry == null || definition == null) return;
            Registry.Register(definition);
            if (launcher != null)
            {
                ShowLauncher();
            }
        }

        public void BuildUI()
        {
            if (canvasRoot != null) return;
            if (Config != null && Config.PresentationPrefab != null)
            {
                canvasRoot = Instantiate(Config.PresentationPrefab, transform, false);
                canvasRoot.name = "PhoneUIRoot";
                var presentationCanvas = canvasRoot.GetComponent<Canvas>();
                if (presentationCanvas != null) presentationCanvas.sortingOrder = 1000;
                phoneDeviceRoot = FindChild(canvasRoot.transform, "PhoneDevice")?.gameObject;
                screenDimRoot = FindChild(canvasRoot.transform, "FullscreenDim")?.gameObject;
                phoneRoot = FindChild(canvasRoot.transform, "LauncherContent")?.gameObject;
                notificationLayer = FindChild(canvasRoot.transform, "NotificationLayer")?.gameObject;
                if (phoneRoot == null || phoneDeviceRoot == null)
                {
                    Debug.LogError("Phone presentation prefab is missing PhoneDevice or LauncherContent.", this);
                    SafeDestroy(canvasRoot);
                    canvasRoot = null;
                }
            }

            if (canvasRoot == null)
            {
                canvasRoot = PhoneUIBuilder.CreateCanvas("PhoneUIRoot");
                canvasRoot.transform.SetParent(transform, false);
                phoneRoot = PhoneUIBuilder.CreatePanel("PhoneRoot", canvasRoot.transform);
                var phoneRt = phoneRoot.GetComponent<RectTransform>();
                phoneRt.anchorMin = new Vector2(.70f, .04f);
                phoneRt.anchorMax = new Vector2(.98f, .96f);
                phoneRt.offsetMin = phoneRt.offsetMax = Vector2.zero;
                phoneDeviceRoot = phoneRoot;
            }

            var audioFeedback = canvasRoot.GetComponent<PhoneAudioFeedback>();
            if (!audioFeedback) audioFeedback = canvasRoot.AddComponent<PhoneAudioFeedback>();
            audioFeedback.Initialize();

            deviceRect = phoneDeviceRoot.GetComponent<RectTransform>();
            if (!deviceHomePositionCaptured && deviceRect != null)
            {
                deviceHomeAnchoredPosition = deviceRect.anchoredPosition;
                deviceHomePositionCaptured = true;
            }
            deviceCanvasGroup = phoneDeviceRoot.GetComponent<CanvasGroup>();
            if (deviceCanvasGroup == null) deviceCanvasGroup = phoneDeviceRoot.AddComponent<CanvasGroup>();

            if (FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                eventSystem = PhoneUIBuilder.CreateEventSystem("PhoneEventSystem");
                eventSystem.transform.SetParent(transform, false);
            }

            // Launcher presenter
            launcher = CreateLauncherPresenter();

            if (notificationLayer == null)
                notificationLayer = PhoneUIBuilder.CreatePanel("NotificationLayer", canvasRoot.transform);
            notificationPresenter = notificationLayer.AddComponent<NotificationPopupPresenter>();
            if (Notifications != null && Config != null)
                notificationPresenter.Initialize(Notifications, Config, notificationLayer.transform);

            canvasRoot.SetActive(false);
        }

        static Transform FindChild(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindChild(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        void AnimateDevice(bool opening)
        {
            if (deviceRect == null || deviceCanvasGroup == null) return;
            if (!Application.isPlaying)
            {
                deviceCanvasGroup.alpha = opening ? 1f : 0f;
                deviceCanvasGroup.interactable = opening;
                deviceCanvasGroup.blocksRaycasts = opening;
                deviceRect.localScale = Vector3.one;
                return;
            }
            if (transition != null) StopCoroutine(transition);
            transition = StartCoroutine(AnimateDeviceRoutine(opening));
        }

        System.Collections.IEnumerator AnimateDeviceRoutine(bool opening)
        {
            var targetPosition = deviceRect.anchoredPosition;
            var fromPosition = targetPosition + new Vector2(0f, -52f);
            var fromScale = opening ? .94f : 1f;
            var toScale = opening ? 1f : .94f;
            float fromAlpha = opening ? 0f : deviceCanvasGroup.alpha;
            float toAlpha = opening ? 1f : 0f;
            float elapsed = 0f;
            const float duration = .22f;
            deviceCanvasGroup.interactable = opening;
            deviceCanvasGroup.blocksRaycasts = opening;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float eased = 1f - Mathf.Pow(1f - t, 3f);
                deviceRect.anchoredPosition = Vector2.LerpUnclamped(opening ? fromPosition : targetPosition, opening ? targetPosition : fromPosition, eased);
                deviceRect.localScale = Vector3.one * Mathf.Lerp(fromScale, toScale, eased);
                deviceCanvasGroup.alpha = Mathf.Lerp(fromAlpha, toAlpha, eased);
                yield return null;
            }
            deviceRect.anchoredPosition = opening ? targetPosition : fromPosition;
            deviceRect.localScale = Vector3.one * toScale;
            deviceCanvasGroup.alpha = toAlpha;
            transition = null;
        }

        System.Collections.IEnumerator AnimateClose()
        {
            float elapsed = 0f;
            float from = deviceCanvasGroup.alpha;
            while (elapsed < .16f && canvasRoot != null)
            {
                elapsed += Time.unscaledDeltaTime;
                deviceCanvasGroup.alpha = Mathf.Lerp(from, 0f, Mathf.Clamp01(elapsed / .16f));
                yield return null;
            }
            if (canvasRoot != null) canvasRoot.SetActive(false);
            transition = null;
        }

        System.Collections.IEnumerator AnimateFullscreenIn(GameObject root)
        {
            if (root == null) yield break;
            var rect = root.GetComponent<RectTransform>();
            var group = root.GetComponent<CanvasGroup>();
            if (group == null) group = root.AddComponent<CanvasGroup>();
            Vector2 target = rect.anchoredPosition;
            group.alpha = 0f;
            rect.localScale = Vector3.one * .975f;
            float elapsed = 0f;
            while (elapsed < .20f && root != null)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / .20f);
                float eased = 1f - Mathf.Pow(1f - t, 3f);
                group.alpha = eased;
                rect.localScale = Vector3.one * Mathf.Lerp(.975f, 1f, eased);
                rect.anchoredPosition = target + new Vector2(0f, Mathf.Lerp(-18f, 0f, eased));
                yield return null;
            }
            if (root != null)
            {
                group.alpha = 1f;
                rect.localScale = Vector3.one;
                rect.anchoredPosition = target;
            }
        }

        PhoneLauncherPresenter CreateLauncherPresenter()
        {
            // Look up the real progression read model from the composition root
            var root = ManagementCompositionRoot.Instance;
            var progression = root != null && root.Progression != null ? root.Progression.ReadModel : null;
            var supplierAccess = root != null && root.SupplierService != null
                ? (SupplierServiceReadModelAccess)new FinDeMes.Management.Phone.SupplierServiceBadgeAdapter(root.SupplierService, root.Clock)
                : null;
            var employeeAccess = root != null && root.Employees != null
                ? (EmployeeServiceReadModelAccess)new FinDeMes.Management.Phone.EmployeeBadgeAdapter(root.Employees)
                : null;
            // Polleria badge access comes from composition root (callback-based, no cross-assembly type reference)
            var polleriaAccess = root != null ? root.PhonePolleriaAccess : null;

            return new PhoneLauncherPresenter(
                Registry, BadgeService, EconomyReadModel, Clock,
                progression, supplierAccess, employeeAccess, polleriaAccess);
        }

        void OnDestroy()
        {
            if (inputLockToken != null) { inputLockToken.Dispose(); inputLockToken = null; }
            if (pauseToken != null) { pauseToken.Dispose(); pauseToken = null; }
            if (IsOpen)
            {
                Cursor.lockState = cursorLockBeforeOpen;
                Cursor.visible = cursorVisibilityBeforeOpen;
            }
            DestroyCurrentHost();
            if (launcher != null) launcher.Destroy();
            if (canvasRoot != null) SafeDestroy(canvasRoot);
            if (eventSystem != null) SafeDestroy(eventSystem);
        }

        static void SafeDestroy(GameObject go)
        {
            if (go == null) return;
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEngine.Object.DestroyImmediate(go);
            else UnityEngine.Object.Destroy(go);
#else
            UnityEngine.Object.Destroy(go);
#endif
        }
    }
}
