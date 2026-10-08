using System;
using UnityEngine;
using UnityEngine.InputSystem;
using FinDeMes.Management;
using FinDeMes.Core.Development;

namespace FinDeMes.PlayerFoundation
{
    /// <summary>Adapts existing input to movement, camera and technical requests for one local player.</summary>
    [RequireComponent(typeof(PlayerMotor))]
    public sealed class PlayerSession : MonoBehaviour, IDevelopmentPlayerCommands
    {
        /// <summary>References the existing input asset, cloned only to isolate action enable state.</summary>
        public InputActionAsset Actions;
        /// <summary>References the owned camera and scene spawn.</summary>
        public ThirdPersonOrbit View;
        /// <summary>References the home spawn, assigned by the scene installer.</summary>
        public Transform Spawn;
        /// <summary>References the existing activity system.</summary>
        public WorldActivity.ActiveBubble Bubble;
        /// <summary>References the Polleria arrival point used by the existing route proof; it is not rendered as an on-screen waypoint.</summary>
        public Transform PolleriaTarget;
        /// <summary>Gets the movement adapter.</summary>
        public PlayerMotor Motor { get; private set; }
        /// <summary>Gets the authoritative locomotion/animation controller.</summary>
        public PlayerAnimationController Animation { get; private set; }
        /// <summary>Gets transient application request handling.</summary>
        public PlayerInteractionRequests Requests { get; } = new();
        /// <summary>Gets the single currently valid primary interaction.</summary>
        public ContextInteraction Selected { get; private set; }
        /// <summary>Gets whether this session has paused simulation.</summary>
        public bool Paused { get; private set; }
        public PlayerVehicle Vehicle { get; private set; }
        public bool IsInVehicle => Vehicle != null;
        public bool IsHudVisible => gameplayHudVisible;
        public bool HasCharacterVisual => developmentVisual != null && developmentVisual.Current != null;
        public bool IsCharacterVisible
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                return developmentVisual == null || developmentVisual.IsCaptureVisible;
#else
                return true;
#endif
            }
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        bool freeCameraEnabled;
        bool freeCameraReturning;
        Vector3 freeCameraTargetPosition;
        Vector3 freeCameraVelocity;
        Vector3 freeCameraSavedPosition;
        Quaternion freeCameraSavedRotation;
        Vector3 freeCameraReturnStartPosition;
        Quaternion freeCameraReturnStartRotation;
        float freeCameraYaw;
        float freeCameraPitch;
        float freeCameraReturnElapsed;
        const float FreeCameraMoveSpeed = 5f;
        const float FreeCameraSprintMultiplier = 3f;
        const float FreeCameraPositionSmoothTime = .12f;
        const float FreeCameraRotationResponse = 12f;
        const float FreeCameraReturnDuration = .32f;
#endif
        Transform footTarget, footCameraRoot;
        PlayerVisual developmentVisual;
        float footDistance;
        /// <summary>Gets the last technical request feedback for debug validation.</summary>
        public string LastMessage => message;
        InputActionAsset local;
        InputAction move, look, sprint, interact, pause, brake, phone;
        PlayerInputLockService inputLockService;
        float messageUntil;
        string message = "";
        CursorLockMode previousCursor; bool previousVisible;
        readonly RaycastHit[] hits = new RaycastHit[32];
        GUIStyle hudBody, hudSmall, hudEyebrow, hudHeading, hudAmount, hudPrompt, hudKey;
        Texture2D hudRing;
        // Manual pause adapter token. Released automatically when the session disables; never restores Time.timeScale directly.
        IDisposable manualPauseToken;
        // Manual-pause input lock token. Owns cursor visibility for the paused menu; does not affect Time.timeScale.
        IDisposable manualInputLockToken;
        bool manualPauseActive;
        bool gameplayInputDisabled;
        bool gameplayHudVisible = true;
        public bool IsFreeCameraEnabled
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                return freeCameraEnabled || freeCameraReturning;
#else
                return false;
#endif
            }
        }
        public bool CanUseFreeCamera
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                return View && View.Target && !IsInVehicle;
#else
                return false;
#endif
            }
        }
#if UNITY_EDITOR
        /// <summary>Allows the Editor-only traversal proof to drive the same motor without simulated gameplay actors.</summary>
        public bool DevelopmentSteering;
        /// <summary>Supplies the Editor proof's world-space direction.</summary>
        public Vector3 DevelopmentDirection;
#endif
        void OnEnable()
        {
            Motor = GetComponent<PlayerMotor>();
            Animation = GetComponent<PlayerAnimationController>();
            developmentVisual = GetComponentInChildren<PlayerVisual>(true);
            if (!Actions || !View) { Debug.LogError("PlayerSession requires input and camera.", this); enabled = false; return; }
            local = Instantiate(Actions);
            move = local.FindAction("Player/Move", true); look = local.FindAction("Player/Look", true);
            sprint = local.FindAction("Player/Sprint", true); interact = local.FindAction("Player/Interact", true);
            pause = local.FindAction("Player/Pause", true);
            brake = local.FindAction("Player/Jump", true); brake.Enable();
            phone = local.FindAction("Player/Phone", false);
            if (phone != null) phone.Enable();
            move.Enable(); look.Enable(); sprint.Enable(); interact.Enable(); pause.Enable();
            previousCursor = Cursor.lockState; previousVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
            if (Bubble) Bubble.Target = transform;

            BindInputLockService();
        }
        void Start()
        {
            // The management composition root is created after scene objects
            // have received OnEnable. Bind again once runtime bootstrap has run.
            BindInputLockService();
            if (Spawn) Motor.Relocate(Spawn.position, Spawn.rotation);
            View.Snap();
        }
        void Update()
        {
            // Supports scenes where a player is enabled before the bootstrap root.
            if (inputLockService == null) BindInputLockService();
            if (pause == null) return;
            if (phone != null && phone.WasPressedThisFrame())
            {
                var root = ManagementCompositionRoot.Instance;
                if (root != null && root.Phone != null) root.Phone.Toggle();
                if (gameplayInputDisabled) return;
            }
            if (pause.WasPressedThisFrame()) SetPaused(!Paused);
            if (Paused) return;
            if (gameplayInputDisabled) return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (freeCameraEnabled)
            {
                UpdateFreeCameraInput(Time.unscaledDeltaTime);
                return;
            }
            if (freeCameraReturning) return;
#endif
            var input = move.ReadValue<Vector2>();
            if (Vehicle)
            {
                Vehicle.Drive.SetInput(input, brake.IsPressed());
                View.Look(look.ReadValue<Vector2>(), look.activeControl != null && look.activeControl.device is Gamepad, Time.deltaTime);
                if (interact.WasPressedThisFrame()) Vehicle.TryExit();
                return;
            }
            var forward = Vector3.ProjectOnPlane(View.transform.forward, Vector3.up).normalized;
            var right = Vector3.Cross(Vector3.up, forward);
            var direction = forward * input.y + right * input.x;
            bool running = sprint.IsPressed();
#if UNITY_EDITOR
            if (DevelopmentSteering) { direction = DevelopmentDirection; running = true; }
#endif
            Motor.Tick(direction, running, Time.deltaTime);
            View.Look(look.ReadValue<Vector2>(), look.activeControl != null && look.activeControl.device is Gamepad, Time.deltaTime);
            RefreshSelection();
            if (interact.WasPressedThisFrame())
            {
                if (Selected) Selected.Invoke(this); else ShowMessage("Acércate a un punto de interacción.");
            }
        }
        void LateUpdate()
        {
            if (View && !Paused && !gameplayInputDisabled)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (freeCameraEnabled) TickFreeCamera(Time.unscaledDeltaTime);
                else if (freeCameraReturning) TickFreeCameraReturn(Time.unscaledDeltaTime);
                else View.Tick(Time.deltaTime);
#else
                View.Tick(Time.deltaTime);
#endif
            }
            PinkInteractionMarker.TickAll(this);
        }
        public void BeginDriving(PlayerVehicle vehicle)
        {
            Vehicle = vehicle; Selected = null;
            Motor.Capsule.enabled = false; Motor.enabled = false; Animation.SetVisualActive(false);
            footTarget = View.Target; footCameraRoot = View.PlayerRoot; footDistance = View.Distance;
            View.Target = vehicle.CameraTarget; View.PlayerRoot = vehicle.transform; View.Distance = 6.5f; View.Snap();
            if (Bubble) Bubble.Target = vehicle.transform;
            ShowMessage("W/S drive · A/D steer · Space brake · E exit when stopped");
        }

        public bool TryGetHomeSpawn(out Vector3 position, out Quaternion rotation)
        {
            if (Spawn != null && Spawn.gameObject.activeInHierarchy)
            {
                position = Spawn.position;
                rotation = Spawn.rotation;
                return IsFinite(position) && IsFinite(rotation);
            }
            position = default;
            rotation = default;
            return false;
        }

        public bool TryTeleport(Vector3 position, Quaternion rotation, out string result)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (freeCameraEnabled || freeCameraReturning)
            {
                result = "Desactiva la cámara libre antes de teletransportar al jugador.";
                return false;
            }
            if (Vehicle != null)
            {
                result = "Sal del vehículo antes de teletransportarte.";
                return false;
            }
            if (Motor == null || Motor.Capsule == null || !View)
            {
                result = "El jugador no está listo para reubicarse.";
                return false;
            }
            float rotationMagnitude = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
            if (!IsFinite(position) || !IsFinite(rotation) || rotationMagnitude < .0001f)
            {
                result = "Destino inválido.";
                return false;
            }

            Motor.Relocate(position, rotation.normalized);
            Selected = null;
            if (Bubble) Bubble.Target = transform;
            View.Snap();
            RefreshSelection();
            ShowMessage("Teletransporte QA completado.");
            result = "Jugador reubicado.";
            return true;
#else
            result = "Teletransporte QA solo está disponible en Editor/Development Build.";
            return false;
#endif
        }

        static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        static bool IsFinite(Quaternion value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z) &&
            !float.IsNaN(value.w) && !float.IsInfinity(value.w);

        public bool TrySetHudVisible(bool visible, out string result)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            gameplayHudVisible = visible;
            result = visible ? "HUD de juego visible." : "HUD de juego oculto para captura.";
            return true;
#else
            result = "La visibilidad del HUD QA solo está disponible en Editor/Development Build.";
            return false;
#endif
        }

        public bool TrySetCharacterVisible(bool visible, out string result)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (IsInVehicle)
            {
                result = "Sal del vehículo antes de cambiar la visibilidad del personaje.";
                return false;
            }
            if (!developmentVisual || !developmentVisual.Current)
            {
                result = "El modelo visual del jugador no está disponible.";
                return false;
            }

            developmentVisual.SetCaptureVisible(visible);
            result = visible ? "Personaje visible para captura." : "Personaje oculto para captura.";
            return true;
#else
            result = "La visibilidad del personaje QA solo está disponible en Editor/Development Build.";
            return false;
#endif
        }

        public bool TrySetFreeCameraEnabled(bool enabled, out string result)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!View)
            {
                freeCameraEnabled = false;
                freeCameraReturning = false;
                result = "La cámara del jugador no está disponible.";
                return false;
            }

            if (enabled)
            {
                if (freeCameraEnabled)
                {
                    result = "La cámara libre ya está activa.";
                    return true;
                }
                if (!CanUseFreeCamera)
                {
                    result = IsInVehicle
                        ? "Sal del vehículo antes de activar la cámara libre."
                        : "El rig de cámara o su objetivo no están disponibles.";
                    return false;
                }

                freeCameraSavedPosition = View.transform.position;
                freeCameraSavedRotation = View.transform.rotation;
                freeCameraTargetPosition = freeCameraSavedPosition;
                freeCameraVelocity = Vector3.zero;
                var angles = View.transform.eulerAngles;
                freeCameraYaw = angles.y;
                freeCameraPitch = angles.x > 180f ? angles.x - 360f : angles.x;
                freeCameraPitch = Mathf.Clamp(freeCameraPitch, -85f, 85f);
                freeCameraReturning = false;
                freeCameraEnabled = true;
                result = "Cámara libre activa. WASD mueve, ratón mira, Espacio/Ctrl sube o baja y Shift acelera.";
                return true;
            }

            if (!freeCameraEnabled)
            {
                result = freeCameraReturning ? "La cámara ya está volviendo al jugador." : "La cámara libre ya está desactivada.";
                return true;
            }

            freeCameraEnabled = false;
            freeCameraReturning = true;
            freeCameraReturnElapsed = 0f;
            freeCameraReturnStartPosition = View.transform.position;
            freeCameraReturnStartRotation = View.transform.rotation;
            result = "Cámara libre desactivada; volviendo suavemente al jugador.";
            return true;
#else
            result = "La cámara libre QA solo está disponible en Editor/Development Build.";
            return false;
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        void UpdateFreeCameraInput(float deltaTime)
        {
            bool stick = look != null && look.activeControl != null && look.activeControl.device is Gamepad;
            var lookDelta = look != null ? look.ReadValue<Vector2>() : Vector2.zero;
            float sensitivity = stick ? View.StickSensitivity * deltaTime : View.MouseSensitivity;
            freeCameraYaw += lookDelta.x * sensitivity;
            freeCameraPitch = Mathf.Clamp(freeCameraPitch - lookDelta.y * sensitivity, -85f, 85f);

            var orientation = Quaternion.Euler(freeCameraPitch, freeCameraYaw, 0f);
            var input = move != null ? move.ReadValue<Vector2>() : Vector2.zero;
            Vector3 direction = orientation * new Vector3(input.x, 0f, input.y);
            float vertical = 0f;
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.spaceKey.isPressed) vertical += 1f;
                if (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed) vertical -= 1f;
            }
            direction += Vector3.up * vertical;
            if (direction.sqrMagnitude > 1f) direction.Normalize();

            float speed = FreeCameraMoveSpeed * (sprint != null && sprint.IsPressed() ? FreeCameraSprintMultiplier : 1f);
            freeCameraTargetPosition += direction * speed * deltaTime;
        }

        void TickFreeCamera(float deltaTime)
        {
            var cameraTransform = View.transform;
            cameraTransform.position = Vector3.SmoothDamp(
                cameraTransform.position,
                freeCameraTargetPosition,
                ref freeCameraVelocity,
                FreeCameraPositionSmoothTime,
                Mathf.Infinity,
                deltaTime);
            var targetRotation = Quaternion.Euler(freeCameraPitch, freeCameraYaw, 0f);
            float response = 1f - Mathf.Exp(-FreeCameraRotationResponse * deltaTime);
            cameraTransform.rotation = Quaternion.Slerp(cameraTransform.rotation, targetRotation, response);
        }

        void TickFreeCameraReturn(float deltaTime)
        {
            freeCameraReturnElapsed += deltaTime;
            float t = Mathf.Clamp01(freeCameraReturnElapsed / FreeCameraReturnDuration);
            float eased = t * t * (3f - 2f * t);
            View.transform.SetPositionAndRotation(
                Vector3.LerpUnclamped(freeCameraReturnStartPosition, freeCameraSavedPosition, eased),
                Quaternion.Slerp(freeCameraReturnStartRotation, freeCameraSavedRotation, eased));
            if (t < 1f) return;
            freeCameraReturning = false;
            freeCameraVelocity = Vector3.zero;
        }
#endif

        public void EndDriving(Vector3 position, Quaternion rotation)
        {
            Vehicle = null; Motor.enabled = true; Motor.Relocate(position, rotation); Animation.SetVisualActive(true);
            View.Target = footTarget; View.PlayerRoot = footCameraRoot; View.Distance = footDistance; View.Snap();
            if (Bubble) Bubble.Target = transform;
            ShowMessage("On foot");
        }
        /// <summary>Chooses one nearby, facing and line-of-sight-valid interaction without scene searches.</summary>
        public void RefreshSelection()
        {
            Selected = null; float best = float.PositiveInfinity;
            if (Vehicle) return;
            var origin = transform.position + Vector3.up;
            foreach (var item in ContextInteraction.Available)
            {
                if (!item || !item.IsAvailable) continue;
                var delta = item.transform.position - origin;
                float distance = delta.magnitude;
                if (distance > item.Range || distance >= best) continue;
                var flat = Vector3.ProjectOnPlane(delta, Vector3.up);
                if (flat.sqrMagnitude > .01f && Vector3.Dot(transform.forward, flat.normalized) < item.MinimumFacingDot) continue;
                bool blocked = false;
                int count = Physics.RaycastNonAlloc(origin, delta.normalized, hits, Mathf.Max(0, distance - .15f), ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < count; i++)
                    if (!hits[i].transform.IsChildOf(transform) && !hits[i].transform.IsChildOf(item.transform)) { blocked = true; break; }
                if (!blocked) { best = distance; Selected = item; }
            }
        }
        /// <summary>Displays short-lived technical feedback.</summary>
        /// <param name="value">The result message.</param>
        public void ShowMessage(string value) { message = value; messageUntil = Time.unscaledTime + 5; }
        /// <summary>Manual pause adapter. Routes the Esc toggle through GameClockService so other pause reasons compose.</summary>
        /// <param name="value">Whether the manual pause is being acquired or released.</param>
        public void SetPaused(bool value)
        {
            if (Paused == value) return;
            Paused = value;
            manualPauseActive = value;
            var root = ManagementCompositionRoot.Instance;
            if (value)
            {
                if (root != null && root.Clock != null && manualPauseToken == null)
                    manualPauseToken = root.Clock.AcquirePause(PauseReason.Manual, "PlayerSession");
                if (root != null && root.InputLock != null && manualInputLockToken == null)
                    manualInputLockToken = root.InputLock.Acquire("PlayerSession.Pause");
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                manualPauseToken?.Dispose(); manualPauseToken = null;
                manualInputLockToken?.Dispose(); manualInputLockToken = null;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }
        void OnDisable()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if ((freeCameraEnabled || freeCameraReturning) && View)
                View.transform.SetPositionAndRotation(freeCameraSavedPosition, freeCameraSavedRotation);
            freeCameraEnabled = false;
            freeCameraReturning = false;
#endif
            if (inputLockService != null)
            {
                inputLockService.Locked -= OnInputLocked;
                inputLockService.Unlocked -= OnInputUnlocked;
                inputLockService = null;
            }
            if (gameplayInputDisabled) RestoreGameplayInput();
            // Release any outstanding manual pause / input lock tokens. Do not touch Time.timeScale directly; the composition root owns that restoration on quit.
            if (manualPauseToken != null) { manualPauseToken.Dispose(); manualPauseToken = null; }
            if (manualInputLockToken != null) { manualInputLockToken.Dispose(); manualInputLockToken = null; }
            Paused = false;
            manualPauseActive = false;
            if (local) { local.Disable(); Destroy(local); }
            Cursor.lockState = previousCursor; Cursor.visible = previousVisible;
        }

        void BindInputLockService()
        {
            if (inputLockService != null) return;
            var root = ManagementCompositionRoot.Instance;
            if (root == null || root.InputLock == null) return;
            inputLockService = root.InputLock;
            inputLockService.Locked += OnInputLocked;
            inputLockService.Unlocked += OnInputUnlocked;
            if (inputLockService.IsLocked) OnInputLocked();
        }

        void OnInputLocked()
        {
            if (gameplayInputDisabled) return;
            gameplayInputDisabled = true;
            move?.Disable();
            look?.Disable();
            sprint?.Disable();
            interact?.Disable();
            brake?.Disable();
        }

        void OnInputUnlocked()
        {
            if (!gameplayInputDisabled) return;
            RestoreGameplayInput();
        }

        void RestoreGameplayInput()
        {
            gameplayInputDisabled = false;
            move?.Enable();
            look?.Enable();
            sprint?.Enable();
            interact?.Enable();
            brake?.Enable();
        }
        void OnGUI()
        {
            var managementRoot = FinDeMes.Management.ManagementCompositionRoot.Instance;
            if (managementRoot != null && managementRoot.Phone != null && managementRoot.Phone.IsOpen) return;
            if (!gameplayHudVisible && !Paused) return;
            EnsureHudVisuals();
            var oldColor = GUI.color;
            var oldContent = GUI.contentColor;
            // Keep the HUD legible in the smaller Game-view sizes commonly
            // used while iterating in the Editor, without letting it dominate
            // the viewport at desktop resolutions.
            float scale = Mathf.Clamp(Mathf.Min(Screen.width / 1280f, Screen.height / 720f), .7f, 1.5f);
            var old = GUI.matrix; GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * scale);
            // IMGUI may inherit a tinted color from another OnGUI handler.
            // Start this HUD from neutral so its own palette stays predictable.
            GUI.color = Color.white;
            GUI.contentColor = Color.white;
            float width = Screen.width / scale, height = Screen.height / scale;
            if (gameplayHudVisible)
            {
                var business = FinDeMes.Polleria.PolleriaTier0.Current;
                bool nearbyBusiness = business && (transform.position - business.transform.position).sqrMagnitude < 2500;

                DrawObjectiveCard(24f, 22f, width, business, nearbyBusiness);
                DrawStatusPanel(width, business, nearbyBusiness);

                if (Selected && !Paused)
                {
                    float pulse = .5f + .5f * Mathf.Sin(Time.unscaledTime * 4.2f);
                    var promptRect = new Rect(width * .5f - 235f, height - 96f, 470f, 54f);
                    DrawHudCard(promptRect, new Color(.97f, .985f, .99f, .97f), new Color(.18f, .72f, .63f, .85f + pulse * .15f));
                    string binding = interact.GetBindingDisplayString(InputBinding.DisplayStringOptions.DontIncludeInteractions);
                    DrawHudCard(new Rect(promptRect.x + 12f, promptRect.y + 10f, 52f, 34f), new Color(.18f, .72f, .63f, .98f), Color.clear);
                    GUI.Label(new Rect(promptRect.x + 12f, promptRect.y + 10f, 52f, 34f), binding, hudKey);
                    GUI.Label(new Rect(promptRect.x + 78f, promptRect.y + 7f, promptRect.width - 92f, 40f), Selected.Prompt, hudPrompt);
                }

                DrawControlHints(width, height);
                if (Time.unscaledTime < messageUntil)
                {
                    float toastWidth = Mathf.Min(390f, width - 40f);
                    var toast = new Rect(width - toastWidth - 22f, height - 74f, toastWidth, 46f);
                    DrawHudCard(toast, new Color(.99f, .985f, .96f, .98f), new Color(.90f, .56f, .20f, .98f));
                    GUI.Label(new Rect(toast.x + 16f, toast.y + 4f, toast.width - 26f, toast.height - 8f), message, hudBody);
                }
            }
            if (Paused)
            {
                GUI.color = new Color(.008f, .018f, .026f, .72f);
                GUI.DrawTexture(new Rect(0f, 0f, width, height), Texture2D.whiteTexture);
                GUI.color = Color.white;
                var pauseCard = new Rect(width * .5f - 190f, height * .5f - 94f, 380f, 188f);
                DrawHudCard(pauseCard, new Color(.97f, .985f, .99f, .99f), new Color(.18f, .72f, .63f));
                GUI.Label(new Rect(pauseCard.x + 26f, pauseCard.y + 22f, 330f, 42f), "PAUSA", hudHeading);
                GUI.Label(new Rect(pauseCard.x + 26f, pauseCard.y + 68f, 330f, 30f), "Tómate un momento. Tu partida está a salvo.", hudSmall);
                GUI.color = new Color(.22f, .82f, .69f, 1f);
                if (GUI.Button(new Rect(pauseCard.x + 26f, pauseCard.y + 120f, 328f, 44f), "CONTINUAR", hudKey)) SetPaused(false);
            }
            GUI.matrix = old;
            GUI.color = oldColor; GUI.contentColor = oldContent;
        }

        void EnsureHudVisuals()
        {
            if (hudBody == null)
            {
                hudBody = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true, alignment = TextAnchor.MiddleLeft };
                hudSmall = new GUIStyle(hudBody) { fontSize = 12 };
                hudEyebrow = new GUIStyle(hudBody) { fontSize = 10, fontStyle = FontStyle.Bold, richText = false };
                hudHeading = new GUIStyle(hudBody) { fontSize = 23, fontStyle = FontStyle.Bold };
                hudAmount = new GUIStyle(hudBody) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
                hudPrompt = new GUIStyle(hudBody) { fontSize = 16, fontStyle = FontStyle.Bold };
                hudKey = new GUIStyle(hudBody) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            }

            // Also restyle retained IMGUI styles after hot recompiles/domain
            // transitions; Unity can preserve the style object itself.
            SetHudTextColor(hudBody, new Color(.09f, .15f, .23f));
            SetHudTextColor(hudSmall, new Color(.36f, .43f, .51f));
            SetHudTextColor(hudEyebrow, new Color(.18f, .48f, .48f));
            SetHudTextColor(hudHeading, new Color(.09f, .15f, .23f));
            SetHudTextColor(hudAmount, new Color(.08f, .48f, .38f));
            SetHudTextColor(hudPrompt, new Color(.09f, .15f, .23f));
            SetHudTextColor(hudKey, Color.white);

            // GUIStyle instances can survive an editor/play transition while
            // non-serialized HideAndDontSave textures are destroyed by Unity.
            // Treat the ring as its own lifetime so HUD drawing never receives null.
            if (hudRing == null) hudRing = CreateHudRingTexture();
        }

        static void SetHudTextColor(GUIStyle style, Color color)
        {
            style.normal.textColor = color;
            style.hover.textColor = color;
            style.active.textColor = color;
            style.focused.textColor = color;
            style.onNormal.textColor = color;
            style.onHover.textColor = color;
            style.onActive.textColor = color;
            style.onFocused.textColor = color;
        }

        void DrawObjectiveCard(float x, float y, float screenWidth, FinDeMes.Polleria.PolleriaTier0 business, bool nearbyBusiness)
        {
            const float cardWidth = 422f;
            var rect = new Rect(x, y, Mathf.Min(cardWidth, screenWidth - 390f), 110f);
            DrawHudCard(rect, new Color(.97f, .985f, .99f, .97f), new Color(.18f, .72f, .63f));
            DrawHudCard(new Rect(rect.x + 17f, rect.y + 19f, 42f, 42f), new Color(.88f, .96f, .95f, 1f), Color.clear);
            DrawRotatingHudRing(new Rect(rect.x + 25f, rect.y + 27f, 26f, 26f), Time.unscaledTime * 18f, new Color(.13f, .63f, .55f, .98f));
            DrawHudDiamond(new Vector2(rect.x + 38f, rect.y + 40f), 9f, new Color(.09f, .43f, .41f, 1f));
            GUI.Label(new Rect(rect.x + 72f, rect.y + 10f, rect.width - 90f, 20f), Vehicle ? "RUTA ACTIVA" : "GUÍA DEL NEGOCIO · SIGUIENTE PASO", hudEyebrow);
            string heading = Requests.PolleriaReached
                ? "Ruta inicial completada"
                : Vehicle ? "Conduce hasta la pollería"
                : nearbyBusiness && business ? business.OpeningObjective
                : "Llega a la pollería";
            string detail = Requests.PolleriaReached
                ? "Continúa explorando para encontrar nuevas actividades."
                : Vehicle ? "Sigue el indicador y mantén la vía libre."
                : nearbyBusiness ? "Sigue el indicador y acércate para interactuar."
                : "Sal de casa y sigue la guía hasta tu negocio.";
            GUI.Label(new Rect(rect.x + 72f, rect.y + 32f, rect.width - 90f, 31f), heading, hudPrompt);
            GUI.Label(new Rect(rect.x + 72f, rect.y + 68f, rect.width - 90f, 28f), detail, hudSmall);
        }

        void DrawStatusPanel(float screenWidth, FinDeMes.Polleria.PolleriaTier0 business, bool nearbyBusiness)
        {
            var root = FinDeMes.Management.ManagementCompositionRoot.Instance;
            bool showBusiness = nearbyBusiness && business;
            float panelWidth = showBusiness ? 350f : 270f;
            float panelHeight = showBusiness ? 106f : 78f;
            var rect = new Rect(screenWidth - panelWidth - 22f, 22f, panelWidth, panelHeight);
            DrawHudCard(rect, new Color(.97f, .985f, .99f, .97f), Color.clear);
            int minute = root != null && root.Clock != null ? Mathf.Clamp((int)root.Clock.CurrentGameMinute, 0, 1439) : 0;
            int day = root != null && root.Clock != null ? root.Clock.CurrentDay : 1;
            GUI.Label(new Rect(rect.x + 16f, rect.y + 9f, panelWidth - 32f, 19f), "DÍA " + day.ToString("00") + "   ·   " + (minute / 60).ToString("00") + ":" + (minute % 60).ToString("00"), hudEyebrow);
            long personal = root != null && root.Economy != null ? root.Economy.ReadModel.GetBalance(FinDeMes.Management.AccountId.PersonalCash) : 0L;
            float personalWidth = showBusiness ? panelWidth - 176f : panelWidth - 32f;
            GUI.Label(new Rect(rect.x + 16f, rect.y + 28f, personalWidth, 15f), "EFECTIVO PERSONAL", hudEyebrow);
            GUI.Label(new Rect(rect.x + 16f, rect.y + 42f, personalWidth, 27f), "S/ " + (personal / 100f).ToString("N2"), hudAmount);
            if (showBusiness)
            {
                float columnX = rect.x + panelWidth - 152f;
                GUI.Label(new Rect(columnX, rect.y + 28f, 136f, 15f), "CAJA DEL NEGOCIO", hudEyebrow);
                long company = root != null && root.Economy != null ? root.Economy.ReadModel.GetBalance(FinDeMes.Management.AccountId.BusinessCash) : 0L;
                GUI.Label(new Rect(columnX, rect.y + 42f, 136f, 27f), "S/ " + (company / 100f).ToString("N2"), hudAmount);
                string state = business.State.Business switch
                {
                    FinDeMes.Polleria.PolleriaTier0.BusinessState.Preparing => "PREPARACIÓN",
                    FinDeMes.Polleria.PolleriaTier0.BusinessState.Open => "ATENCIÓN ABIERTA",
                    FinDeMes.Polleria.PolleriaTier0.BusinessState.Closing => "CERRANDO",
                    _ => "CERRADO",
                };
                GUI.Label(new Rect(rect.x + 16f, rect.y + 77f, panelWidth - 32f, 19f), state + "   ·   POLLO " + business.State.Chicken + "   PAPAS " + business.State.Sides + "   BEBIDAS " + business.State.Drinks, hudSmall);
            }
        }

        void DrawControlHints(float screenWidth, float screenHeight)
        {
            if (Paused || gameplayInputDisabled) return;
            float y = screenHeight - 54f;
            float x = 22f;
            DrawControlChip(ref x, y, "WASD", "MOVER");
            DrawControlChip(ref x, y, "SHIFT", "CORRER");
            if (phone != null) DrawControlChip(ref x, y, "P", "TELÉFONO");
        }

        void DrawControlChip(ref float x, float y, string binding, string label)
        {
            float keyWidth = Mathf.Clamp(24f + binding.Length * 7f, 38f, 76f);
            DrawHudCard(new Rect(x, y, keyWidth, 27f), new Color(.97f, .985f, .99f, .92f), Color.clear);
            GUI.Label(new Rect(x + 2f, y + 1f, keyWidth - 4f, 25f), binding, hudKey);
            x += keyWidth + 7f;
            float labelWidth = Mathf.Clamp(36f + label.Length * 5f, 56f, 92f);
            GUI.Label(new Rect(x, y + 1f, labelWidth, 25f), label, hudSmall);
            x += labelWidth + 14f;
        }

        static void DrawHudDiamond(Vector2 center, float size, Color color)
        {
            var previous = GUI.matrix;
            GUIUtility.RotateAroundPivot(45f, center);
            GUI.color = color;
            GUI.DrawTexture(new Rect(center.x - size * .5f, center.y - size * .5f, size, size), Texture2D.whiteTexture);
            GUI.matrix = previous;
            GUI.color = Color.white;
        }

        void DrawRotatingHudRing(Rect rect, float angle, Color color)
        {
            var previous = GUI.matrix;
            // Apply the rotation in the current GUI coordinate space. Using
            // GUIUtility.RotateAroundPivot with the scaled HUD matrix shifts
            // small icons away from their anchors on non-native resolutions.
            var pivot = rect.center;
            GUI.matrix = previous * Matrix4x4.TRS(pivot, Quaternion.Euler(0f, 0f, angle), Vector3.one) * Matrix4x4.Translate(-pivot);
            GUI.color = color;
            GUI.DrawTexture(rect, hudRing);
            GUI.matrix = previous;
            GUI.color = Color.white;
        }

        static void DrawHudCard(Rect rect, Color fill, Color accent)
        {
            GUI.color = fill;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            if (accent.a > 0f)
            {
                GUI.color = accent;
                GUI.DrawTexture(new Rect(rect.x, rect.y, 3f, rect.height), Texture2D.whiteTexture);
            }
            GUI.color = Color.white;
        }

        static Texture2D CreateHudRingTexture()
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "GameplayHudObjectiveRing", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var offset = new Vector2(x, y) - new Vector2((size - 1) * .5f, (size - 1) * .5f);
                    float radius = offset.magnitude / (size * .5f);
                    float alpha = Mathf.Clamp01(1f - Mathf.Abs(radius - .72f) / .13f) * .78f;
                    float angle = Mathf.Atan2(offset.y, offset.x) * Mathf.Rad2Deg;
                    float sweep = Mathf.Abs(Mathf.DeltaAngle(angle, 0f));
                    if (sweep < 16f && Mathf.Abs(radius - .72f) < .16f) alpha = 1f;
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }
    }
}
