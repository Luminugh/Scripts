using System;
using System.Collections.Generic;
using System.Linq;
using FinDeMes.Core.Audio;
using FinDeMes.Management;
using FinDeMes.Management.Phone;
using FinDeMes.PlayerFoundation;
using UnityEngine;
using UnityEngine.AI;

namespace FinDeMes.Polleria
{
    public sealed class PolleriaTier0 : MonoBehaviour
    {
        public enum BusinessState { Preparing, Open, Closing, ClosedForDay }
        public enum Product { PolloSimple, PolloConPapa, Gaseosa }
        public enum OrderState
        {
            WaitingForOrder, WaitingForRegistration, Registered, OrderTaken, Preparing, ReadyToDeliverToCounter,
            WaitingAtCounter, ReadyToPickup, CarriedToTable, DeliveredToTable,
            ReadyForTable, Eating, WaitingToPay, PaymentInProgress, Paid, Completed, Cancelled
        }
        public enum PolleriaWorkType { TakeOrder, RegisterOrder, CookOrder, KitchenDropoff, CounterPickup, DeliverOrder, CashierPayment }
        [Serializable] public sealed class EmployeeTaskTimings
        {
            [Min(.1f)] public float WaiterTakeOrderSeconds = 5f, OrderRegistrationSeconds = 2.5f;
            [Min(0f)] public float WaiterTakeOrderAdditionalSecondsPerServing = .5f;
            [Min(.1f)] public float ChefCookingSeconds = 10f, KitchenDropoffSeconds = 1.5f;
            [Min(0f)] public float ChefAdditionalSecondsPerServing = 2f;
            [Min(.1f)] public float CounterPickupSeconds = 1.25f, TableDeliverySeconds = 2f, CashierPaymentSeconds = 4f;
            [Min(.05f)] public float RecoverySeconds = .5f, SearchSeconds = .25f;
        }
        public enum TableState { Available, Occupied, NeedsCleaning }
        [Serializable] public sealed class MenuPrices
        {
            [Min(0)] public long PolloSimpleMinor = 1500;
            [Min(0)] public long PolloConPapaMinor = 1800;
            [Min(0)] public long GaseosaMinor = 500;
        }
        public sealed class Order { public int Id; public Product Product; public OrderState State; public float Created, Quality; public bool Delivered, PaymentCompleted; public CustomerGroup Group; public int OriginGroupId; public string OriginTableId; public string CookingReservationId, CarriedByWorkerId; public int ServingCount = 1; }
        public sealed class DayState
        {
            public BusinessState Business = BusinessState.Preparing;
            public int Chicken = 8, Sides = 8, Drinks = 8, Served, Lost, Unhappy, NextOrder = 1;
            /// <summary>Non-authoritative session projection of authoritative Polleria sales (managed by EconomyService). Updates after each successful CompletePayment. Do not treat as money.</summary>
            public float Sales;
            /// <summary>Non-authoritative session projection of authoritative Polleria costs (managed by EconomyService). Do not treat as money.</summary>
            public float Costs;
            public float Cleanliness = 100;
            public bool StockChecked, GrillPrepared, RegisterOpen;
            public readonly List<Order> Orders = new();
            public TableState[] Tables = Array.Empty<TableState>();
            /// <summary>Tracks the single order the player is currently carrying from the counter to its table.</summary>
            public Order CarriedOrder;
            public bool HasReady => Orders.Any(o => o.State == OrderState.ReadyToDeliverToCounter || o.State == OrderState.ReadyToPickup || o.State == OrderState.CarriedToTable);
            public float Net => Sales - Costs;
        }
        public enum GroupStage { Entering, WaitingForOrder, WaitingForFood, ReadyForDelivery, Eating, Payment, Exit }
        public sealed class CustomerGroup
        {
            public readonly int Id; public readonly List<PolleriaCustomer> Customers = new();
            public PolleriaTable Table; public int TableIndex = -1; public Order Order;
            public PolleriaCustomer PayingCustomer;
            public int GroupSize => Customers.Count;
            public GroupStage Stage = GroupStage.Entering; public float EatingElapsed; public bool PaymentCompleted;
            public CustomerGroup(int id) { Id = id; }
        }

        public PolleriaAnchors Anchors;
        /// <summary>Authoritative Polleria stock state owned by the D2 management core. DayState.Chicken/Sides/Drinks are non-authoritative session projections of this state.</summary>
        [NonSerialized] public PolleriaInventoryState InventoryState;
        public GameObject CustomerVisual;
        public NPCVisualCatalog CustomerVisualCatalog;
        public Transform[] QueuePositions = Array.Empty<Transform>();
        public Transform CustomerServiceArea, CustomerWaitingArea, CustomerPickupArea;
        [Header("Counter food hand-off anchors")]
        public Transform CounterKitchenDropoffAnchor, CounterCustomerPickupAnchor;
        [Tooltip("Surface-level anchor for the visible order plates. Place this on the actual counter top.")]
        public Transform CounterPlateSurfaceAnchor;
        [Tooltip("Project-owned Humanoid controller used by the physical Polleria employee visuals.")]
        public RuntimeAnimatorController EmployeeLocomotionController;
        public Sprite[] CustomerStatusSprites = Array.Empty<Sprite>();
        public PolleriaTable[] MappedTables = Array.Empty<PolleriaTable>();
        [Min(1f)] public float EatingDurationSeconds = 20f;
        public EmployeeTaskTimings EmployeeTimings = new();
        [Header("Precios de venta (céntimos)")]
        public MenuPrices Prices = new();
        [Header("Customer patience")]
        [Min(.1f)] public float ImpatientAfterSeconds = 8f;
        [Min(.1f)] public float AngryAfterSeconds = 16f;
        public float CustomerInterval = 7f;
        public int MaxCustomers = 4;
        [Header("Customer group sizing")]
        [Range(1, 4)] public int MinimumCustomerGroupSize = 1;
        [Range(1, 4)] public int MaximumCustomerGroupSize = 4;
        public DayState State { get; private set; } = new();
        readonly List<PolleriaCustomer> customers = new();
        readonly List<CustomerGroup> groups = new();
        GrillDelegationController grillDelegation;
        PolleriaAudioController audioController;
        ContextInteraction cashierInteraction;
        ContextInteraction orderStationInteraction;
        readonly Dictionary<string, string> workReservations = new(StringComparer.Ordinal);
        readonly Dictionary<int, int> counterSlotsByOrder = new();
        readonly Dictionary<int, PolleriaPlatePresentation> orderPlates = new();
        Transform[] counterPlateSlots = Array.Empty<Transform>();
        int manualOrderRegistrationId = -1;
        bool phoneOperationsAppRegistered;
        float nextCustomer; int nextGroupId = 1;
        static PolleriaTier0 current;
        public static PolleriaTier0 Current => current;
        public bool IsOpen => State.Business == BusinessState.Open;
        public IReadOnlyList<Order> ActiveOrders => State.Orders;
        public IReadOnlyList<CustomerGroup> ActiveGroups => groups;
        public IReadOnlyList<PolleriaCustomer> ActiveCustomers => customers;
        public void PlayGrillUseFeedback() => audioController?.PlayGrillUse();
        public bool HasActiveEmployee(EmployeeRole role)
        {
            var root = ManagementCompositionRoot.Instance;
            var employee = root != null ? root.Employees?.GetReadModel(role) : null;
            return employee != null && employee.HasEmployee && root.Employees.IsEmployeeOnShift(employee.EmployeeId);
        }
        public bool HasDirtyTables => MappedTables != null && MappedTables.Any(t => t && t.State == TableState.NeedsCleaning);
        public string OpeningObjective => State.Business == BusinessState.Preparing
            ? !State.StockChecked ? "Revisar el inventario"
            : !State.GrillPrepared ? "Preparar la parrilla"
            : !State.RegisterOpen ? "Abrir la caja"
            : "Abrir la pollería"
            : State.Business == BusinessState.Open ? "Atender pedidos y servir en las mesas" : "Día cerrado · revisar resultados";

        void Awake()
        {
            current = this;
            audioController = GetComponentInChildren<PolleriaAudioController>(true);
            if (!Anchors) Anchors = GetComponent<PolleriaAnchors>();
            ResolveCounterServiceAnchors();
            if (MappedTables == null || MappedTables.Length == 0)
                MappedTables = FindObjectsByType<PolleriaTable>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(t => t.TableId).ToArray();
            for (int i = 0; i < MappedTables.Length; i++)
            {
                MappedTables[i].EnsureInteractionMarker();
                if (MappedTables[i].DeliveryInteraction) MappedTables[i].DeliveryInteraction.StationKey = "TableDelivery_" + i;
                MappedTables[i].SetOpen(false);
            }
            if (ManagementCompositionRoot.Instance != null && ManagementCompositionRoot.Instance.Employees != null)
            {
                grillDelegation = GetComponent<GrillDelegationController>() ?? gameObject.AddComponent<GrillDelegationController>();
                grillDelegation.Initialize(this, ManagementCompositionRoot.Instance.Employees);
            }
            DisableLegacyStation("Pickup");
            DisableLegacyStation("CleanTable");
            DisableLegacyStation("TableInteraction");
            EnsureEmployeeStations();
            ConfigureOrderStationInteraction();
            ConfigureCounterInteractions();
            ConfigureCashierInteraction();
            ResetTableStateArray();
            // Initialize the authoritative inventory state once. DayState stock values are non-authoritative session projections.
            if (InventoryState == null) InventoryState = new PolleriaInventoryState(State.Chicken, State.Sides, State.Drinks);
            SyncDayStockFromInventory();
        }
        void ResolveCounterServiceAnchors()
        {
            var stations = FindObjectsByType<ContextInteraction>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < stations.Length; i++)
            {
                var station = stations[i];
                if (!station) continue;
                if (station.EffectiveStationKey == "CounterKitchenDropoff") CounterKitchenDropoffAnchor = station.transform;
                else if (station.EffectiveStationKey == "CounterCustomerPickup") CounterCustomerPickupAnchor = station.transform;
            }
            if (Anchors)
            {
                // Exact same physical points for employee and player: kitchen side behind
                // the counter, pickup side in front of the counter.
                Anchors.KitchenDropoffEmployee = CounterKitchenDropoffAnchor;
                Anchors.CustomerPickupEmployee = CounterCustomerPickupAnchor;
            }
        }
        void OnDestroy() { if (current == this) current = null; }
        void Start()
        {
            RegisterPhoneOperationsApp();
            ConfigureCounterInteractions();
            ConfigureCashierInteraction();
            var upgrades = ManagementCompositionRoot.Instance != null ? ManagementCompositionRoot.Instance.Upgrades : null;
            if (upgrades != null)
            {
                var presenter = GetComponent<PolleriaUpgradePresenter>() ?? gameObject.AddComponent<PolleriaUpgradePresenter>();
                presenter.Initialize(upgrades, CounterKitchenDropoffAnchor, CounterCustomerPickupAnchor);
            }
            for (int i = 0; i < MappedTables.Length; i++)
            {
                if (!MappedTables[i]) continue;
                MappedTables[i].EnsureInteractionMarker();
                if (MappedTables[i].DeliveryInteraction)
                    MappedTables[i].DeliveryInteraction.StationKey = "TableDelivery_" + i;
            }
        }
        void Update()
        {
            if (!phoneOperationsAppRegistered) RegisterPhoneOperationsApp();
            var root = ManagementCompositionRoot.Instance;
            if (grillDelegation == null && root != null && root.Employees != null)
            {
                grillDelegation = GetComponent<GrillDelegationController>() ?? gameObject.AddComponent<GrillDelegationController>();
                grillDelegation.Initialize(this, root.Employees);
            }
            if (State.Business == BusinessState.Open && Time.time >= nextCustomer && customers.Count < MaxCustomers) { SpawnCustomerGroup(); nextCustomer = Time.time + CustomerInterval; }
            TryAssignWaitingGroups();
            RebuildPaymentQueue();
            for (int i = customers.Count - 1; i >= 0; i--) { if (!customers[i]) { customers.RemoveAt(i); continue; } customers[i].Tick(this, Time.deltaTime); }
            for (int i = groups.Count - 1; i >= 0; i--)
            {
                var group = groups[i]; if (group == null) { groups.RemoveAt(i); continue; }
                if (group.Stage == GroupStage.WaitingForOrder && AllAtSeats(group))
                    foreach (var customer in group.Customers) customer.Stage = PolleriaCustomer.CustomerStage.ReadyToOrder;
                if (group.Stage == GroupStage.Eating) { group.EatingElapsed += Time.deltaTime; if (group.EatingElapsed >= EatingDurationSeconds) BeginPayment(group); }
                if (group.Stage == GroupStage.Exit && group.Table && group.Customers.All(c => !c))
                {
                    group.Table.SetDirty();
                    if (group.TableIndex >= 0 && group.TableIndex < State.Tables.Length) State.Tables[group.TableIndex] = TableState.NeedsCleaning;
                    // Retire the finished group so later groups reusing the same table are
                    // found by TableIndex lookups instead of this stale one.
                    group.Table = null;
                    groups.RemoveAt(i);
                }
            }
            RefreshTableMarkers();
            if (State.Business == BusinessState.Open) State.Cleanliness = Mathf.Max(0, State.Cleanliness - Time.deltaTime * .03f);
        }
        void ResetTableStateArray() { State.Tables = new TableState[MappedTables.Length]; for (int i = 0; i < MappedTables.Length; i++) State.Tables[i] = MappedTables[i].State; }

        void ConfigureCashierInteraction()
        {
            var stations = FindObjectsByType<ContextInteraction>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var station in stations)
            {
                if (station && station.EffectiveStationKey == "Cashier")
                {
                    cashierInteraction = station;
                    station.Action = ContextInteraction.Kind.Polleria;
                    station.Prompt = "Cashier";
                }
            }
        }

        void EnsureEmployeeStations()
        {
            if (!Anchors) return;
            if (!Anchors.OrderStation)
            {
                Transform basis = Anchors.Preparation ? Anchors.Preparation : Anchors.Grill;
                Vector3 position = basis ? basis.position : transform.position;
                Vector3 right = basis ? basis.right : transform.right;
                position -= right * 1.35f;
                var station = new GameObject("OrderStation").transform;
                station.SetParent(Anchors.transform, true);
                station.position = position;
                Anchors.OrderStation = station;
                CreateStationPrimitive(station, "ComandaBase", PrimitiveType.Cube, new Vector3(0f, .48f, 0f), new Vector3(.72f, .92f, .48f), new Color(.31f, .24f, .18f));
                CreateStationPrimitive(station, "ComandaMonitor", PrimitiveType.Cube, new Vector3(0f, 1.08f, .08f), new Vector3(.58f, .4f, .08f), new Color(.11f, .23f, .27f));
                CreateStationPrimitive(station, "ComandaPanel", PrimitiveType.Cube, new Vector3(0f, .88f, .29f), new Vector3(.62f, .04f, .3f), new Color(.52f, .43f, .34f));
            }

            Transform interactionAnchor = Anchors.OrderStation.Find("PlayerInteraction");
            if (!interactionAnchor)
            {
                interactionAnchor = new GameObject("PlayerInteraction").transform;
                interactionAnchor.SetParent(Anchors.OrderStation, false);
                interactionAnchor.localPosition = new Vector3(0f, 0f, -.85f);
            }
            orderStationInteraction = interactionAnchor.GetComponent<ContextInteraction>() ?? interactionAnchor.gameObject.AddComponent<ContextInteraction>();
            orderStationInteraction.Action = ContextInteraction.Kind.Polleria;
            orderStationInteraction.StationKey = "OrderStation";
            orderStationInteraction.Prompt = "Register Order";
            orderStationInteraction.Range = 2.2f;

            Anchors.OrderStationEmployee = EnsureAnchor(Anchors, Anchors.OrderStationEmployee, "OrderStationEmployeeAnchor", Anchors.OrderStation.position + Anchors.OrderStation.forward * .6f);
            Anchors.EmployeeEntrance = Anchors.EmployeeEntrance ? Anchors.EmployeeEntrance : Anchors.ServiceEntrance ? Anchors.ServiceEntrance : Anchors.Entrance;
            Anchors.EmployeeExit = Anchors.EmployeeExit ? Anchors.EmployeeExit : Anchors.Entrance;
            Vector3 waiterDoorIdle = Anchors.Entrance
                ? Anchors.Entrance.position + Anchors.Entrance.forward * 1.5f
                : Anchors.EmployeeEntrance ? Anchors.EmployeeEntrance.position + Anchors.transform.right * 1.4f : Anchors.OrderStation.position;
            Anchors.WaiterIdle = EnsureAnchor(Anchors, Anchors.WaiterIdle, "WaiterIdleAnchor", waiterDoorIdle);
            Anchors.WaiterIdle.position = waiterDoorIdle;
            Anchors.ChefIdle = EnsureAnchor(Anchors, Anchors.ChefIdle, "ChefIdleAnchor", Anchors.Grill ? Anchors.Grill.position : Anchors.OrderStation.position);
            Vector3 cashierBackOfCounter = Anchors.Cashier
                ? Anchors.Cashier.position + Anchors.transform.forward * 3.25f
                : Anchors.OrderStation.position;
            Anchors.CashierWork = EnsureAnchor(Anchors, Anchors.CashierWork, "CashierWorkAnchor", cashierBackOfCounter);
            Anchors.CashierIdle = EnsureAnchor(Anchors, Anchors.CashierIdle, "CashierIdleAnchor", cashierBackOfCounter + Anchors.transform.right * 1.1f);
            Anchors.KitchenDropoffEmployee = Anchors.KitchenDropoffEmployee ? Anchors.KitchenDropoffEmployee : CounterKitchenDropoffAnchor;
            Anchors.CustomerPickupEmployee = Anchors.CustomerPickupEmployee ? Anchors.CustomerPickupEmployee : CounterCustomerPickupAnchor;
            EnsureCounterPlateSlots();
        }

        static Transform EnsureAnchor(PolleriaAnchors owner, Transform existing, string name, Vector3 position)
        {
            if (existing) return existing;
            Transform anchor = owner.transform.Find(name);
            if (!anchor)
            {
                anchor = new GameObject(name).transform;
                anchor.SetParent(owner.transform, true);
                anchor.position = position;
            }
            return anchor;
        }

        static void CreateStationPrimitive(Transform parent, string name, PrimitiveType type, Vector3 localPosition, Vector3 localScale, Color color)
        {
            var part = GameObject.CreatePrimitive(type);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = localScale;
            var collider = part.GetComponent<Collider>();
            if (collider) Destroy(collider);
            var renderer = part.GetComponent<Renderer>();
            if (renderer)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                if (shader) renderer.sharedMaterial = new Material(shader) { color = color };
            }
        }

        void ConfigureOrderStationInteraction()
        {
            if (orderStationInteraction) orderStationInteraction.Prompt = "Register Order";
        }

        void EnsureCounterPlateSlots()
        {
            if (counterPlateSlots.Length == 2 && counterPlateSlots[0] && counterPlateSlots[1]) return;
            counterPlateSlots = new Transform[2];
            Transform slotParent = CounterPlateSurfaceAnchor ? CounterPlateSurfaceAnchor : CounterKitchenDropoffAnchor;
            if (!slotParent) return;
            for (int i = 0; i < counterPlateSlots.Length; i++)
            {
                string name = "CounterPlateSlot" + i;
                Transform slot = slotParent.Find(name);
                if (!slot)
                {
                    slot = new GameObject(name).transform;
                    slot.SetParent(slotParent, false);
                }
                else if (slot.parent != slotParent) slot.SetParent(slotParent, false);
                // The plate roots are centered on their mesh; the authored surface anchor
                // sits just above the real countertop, so these slots stay on the counter.
                slot.localPosition = CounterPlateSurfaceAnchor
                    ? new Vector3((i - .5f) * .8f, 0f, 0f)
                    : new Vector3((i - .5f) * .8f, 1.09f, -1.5f);
                counterPlateSlots[i] = slot;
            }
        }

        void RegisterPhoneOperationsApp()
        {
            ManagementCompositionRoot root = ManagementCompositionRoot.Instance;
            if (phoneOperationsAppRegistered || root == null || root.Phone == null) return;
            root.Phone.RegisterApp(new PhoneAppDefinition(PhoneAppId.Orders, "Pedidos", false,
                () => new PolleriaFlowPresenter(this, () => root.Phone.NavigateTo(PhoneAppId.Staff))));
            phoneOperationsAppRegistered = true;
        }

        void ConfigureCounterInteractions()
        {
            var stations = FindObjectsByType<ContextInteraction>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var station in stations)
            {
                if (!station) continue;
                string key = station.EffectiveStationKey;
                if (key == "CounterKitchenDropoff")
                {
                    station.gameObject.SetActive(true);
                    station.Action = ContextInteraction.Kind.Polleria;
                    station.Prompt = "Deliver Order to Counter";
                }
                else if (key == "CounterCustomerPickup")
                {
                    station.gameObject.SetActive(true);
                    station.Action = ContextInteraction.Kind.Polleria;
                    station.Prompt = "Pick Up Order";
                }
            }
        }

        /// <summary>Copies authoritative inventory values onto DayState for legacy read-only compatibility. DayState remains non-authoritative.</summary>
        void SyncDayStockFromInventory()
        {
            if (InventoryState == null) return;
            State.Chicken = InventoryState.Get(StockItem.Chicken);
            State.Sides = InventoryState.Get(StockItem.Sides);
            State.Drinks = InventoryState.Get(StockItem.Drinks);
        }

        StockCost CostForOrder(Order order)
        {
            int servings = order != null ? Mathf.Max(1, order.ServingCount) : 1;
            switch (order.Product)
            {
                case Product.PolloConPapa: return new StockCost(servings, servings, 0);
                case Product.PolloSimple: return new StockCost(servings, 0, 0);
                case Product.Gaseosa: return new StockCost(0, 0, servings);
                default: return new StockCost(0, 0, 0);
            }
        }
        void RefreshTableMarkers()
        {
            if (cashierInteraction) cashierInteraction.Prompt = HasWaitingPlayerPayment() ? "Serve Customer" : "Cashier";
            if (State.Tables == null || State.Tables.Length != MappedTables.Length) ResetTableStateArray();
            for (int i = 0; i < MappedTables.Length; i++)
            {
                if (!MappedTables[i]) continue;
                MappedTables[i].EnsureInteractionMarker();
                if (MappedTables[i].DeliveryInteraction && MappedTables[i].DeliveryInteraction.StationKey != "TableDelivery_" + i)
                    MappedTables[i].DeliveryInteraction.StationKey = "TableDelivery_" + i;
                State.Tables[i] = MappedTables[i].State;
                MappedTables[i].SetOpen(State.Business == BusinessState.Open);
                var interaction = MappedTables[i].DeliveryInteraction;
                if (!interaction) continue;
                var group = GetActiveGroupForTable(i);
                interaction.Prompt = MappedTables[i].State == TableState.NeedsCleaning ? "Clean Table"
                    : group == null ? "Available Table"
                    : group.Stage == GroupStage.WaitingForOrder && AllAtSeats(group) ? "Take Order"
                    : group.Stage == GroupStage.WaitingForOrder ? "Customer arriving"
                    : group.Stage == GroupStage.ReadyForDelivery && group.Order != null && group.Order.State == OrderState.CarriedToTable ? "Deliver Order to Table"
                    : "Available Table";
            }
        }

        public bool IsStationAvailable(string station)
        {
            bool preparing = State.Business == BusinessState.Preparing, open = State.Business == BusinessState.Open;
            if (station.StartsWith("TableDelivery_", StringComparison.Ordinal))
            {
                int index = ParseTableIndex(station);
                if (!open || index < 0 || index >= MappedTables.Length) return false;
                var table = MappedTables[index];
                if (table.State == TableState.Available) return false;
                if (table.State == TableState.NeedsCleaning) return true;
                var group = GetActiveGroupForTable(index);
                if (group == null) return false;
                if (group.Stage == GroupStage.WaitingForOrder && AllAtSeats(group) && !IsWorkReserved(PolleriaWorkType.TakeOrder, group.Id.ToString())) return true;
                // Delivery is available only for the exact table whose originating order is carried.
                if (group.Stage == GroupStage.ReadyForDelivery && group.Order != null && group.Order.State == OrderState.CarriedToTable
                    && !IsWorkReserved(PolleriaWorkType.DeliverOrder, group.Order.Id.ToString())) return true;
                return false;
            }
            switch (station)
            {
                case "StockStation": return preparing && !State.StockChecked || open;
                case "PrepareGrill": return preparing && State.StockChecked && !State.GrillPrepared;
                case "OpenRegister": return preparing && State.GrillPrepared && !State.RegisterOpen;
                case "OpenBusiness": return preparing && State.RegisterOpen;
                case "OrderStation": return open && State.Orders.Any(o => o != null && o.State == OrderState.WaitingForRegistration
                    && !IsWorkReserved(PolleriaWorkType.RegisterOrder, o.Id.ToString()));
                case "Cashier": return open && HasWaitingPlayerPayment() && !HasActiveEmployee(EmployeeRole.Cashier)
                    && !HasAnyCashierReservation();
                case "Grill": return open && State.Orders.Any(o => o.State == OrderState.Registered && HasIngredients(o)
                    && !IsWorkReserved(PolleriaWorkType.CookOrder, o.Id.ToString()));
                case "CounterKitchenDropoff": return open && FindFreeCounterSlot() >= 0
                    && State.Orders.Any(o => o.State == OrderState.ReadyToDeliverToCounter && !IsWorkReserved(PolleriaWorkType.KitchenDropoff, o.Id.ToString()));
                case "CounterCustomerPickup": return open && !State.Orders.Any(o => o != null && o.State == OrderState.CarriedToTable)
                    && State.Orders.Any(o => o.State == OrderState.ReadyToPickup && !IsWorkReserved(PolleriaWorkType.CounterPickup, o.Id.ToString()));
                case "CloseBusiness": return open;
                case "CleanTable": return MappedTables.Any(t => t && t.State == TableState.NeedsCleaning) || open && State.Cleanliness < 95;
                case "DailyResults": return State.Business == BusinessState.ClosedForDay;
                case "TableInteraction": return false;
                default: return false;
            }
        }
        public string HandleInteraction(ContextInteraction interaction)
        {
            if (!interaction) return "Interacción inválida.";
            string station = interaction.EffectiveStationKey;
            if (!IsStationAvailable(station)) return "This station has no available action yet.";
            if (station.StartsWith("TableDelivery_", StringComparison.Ordinal))
            {
                int tableIndex = ParseTableIndex(station);
                if (tableIndex < 0 || tableIndex >= MappedTables.Length) return "Mesa inválida.";
                if (MappedTables[tableIndex].State == TableState.NeedsCleaning) { string cleaned = CleanTable(tableIndex); RefreshTableMarkers(); return cleaned; }
                var tableGroup = GetActiveGroupForTable(tableIndex);
                if (tableGroup == null) return "Esta mesa no tiene una acción disponible.";
                if (tableGroup.Stage == GroupStage.WaitingForOrder)
                {
                    string target = tableGroup.Id.ToString();
                    if (!TryReserveWork(PolleriaWorkType.TakeOrder, target, "player")) return "Otro trabajador está atendiendo esta mesa.";
                    string taken = TakeOrderAtTable(tableIndex, "player");
                    ReleaseWork(PolleriaWorkType.TakeOrder, target, "player");
                    RefreshTableMarkers(); return taken;
                }
                if (tableGroup.Stage == GroupStage.ReadyForDelivery) { string delivered = DeliverFoodToTable(tableIndex); RefreshTableMarkers(); return delivered; }
                return "Esta mesa no tiene una acción disponible.";
            }
            switch (station)
            {
                case "StockStation": State.StockChecked = true; return "Stock · Pollo: " + State.Chicken + " · Papas: " + State.Sides + " · Bebidas: " + State.Drinks;
                case "PrepareGrill": State.GrillPrepared = true; return "Brasa preparada. Ya puedes abrir la caja.";
                case "OpenRegister": State.RegisterOpen = true; return "Caja abierta.";
                case "OpenBusiness": State.Business = BusinessState.Open; nextCustomer = Time.time + 1; return "Pollería abierta · clientes buscan mesa.";
                case "Grill": { string cooked = CookBatch(); RefreshTableMarkers(); return cooked; }
                case "OrderStation":
                {
                    Order order = State.Orders.FirstOrDefault(o => o != null && o.State == OrderState.WaitingForRegistration
                        && !IsWorkReserved(PolleriaWorkType.RegisterOrder, o.Id.ToString()));
                    if (order == null || !TryReserveWork(PolleriaWorkType.RegisterOrder, order.Id.ToString(), "player")) return "No hay un pedido disponible para registrar.";
                    bool registered = RegisterOrder(order, "player");
                    ReleaseWork(PolleriaWorkType.RegisterOrder, order.Id.ToString(), "player");
                    if (registered) manualOrderRegistrationId = -1;
                    RefreshTableMarkers();
                    return registered ? "Pedido registrado en comanda y enviado a cocina." : "No se pudo registrar el pedido.";
                }
                case "Cashier": return ServeFrontPayerManually();
                case "CounterKitchenDropoff": { string dropped = DeliverPreparedOrderToCounter(); RefreshTableMarkers(); return dropped; }
                case "CounterCustomerPickup": { string picked = PickupReadyOrder(); RefreshTableMarkers(); return picked; }
                case "CleanTable": return "La limpieza se realiza desde el marcador de la mesa.";
                case "CloseBusiness": return CloseBusiness();
                case "DailyResults": return Results();
                default: return "Esta estación aún no está configurada.";
            }
        }
        int ParseTableIndex(string station) { int underscore = station.LastIndexOf('_'); return underscore >= 0 && int.TryParse(station.Substring(underscore + 1), out int index) ? index : -1; }

        void SpawnCustomerGroup()
        {
            int remaining = MaxCustomers - customers.Count;
            if (remaining <= 0) return;
            int largestTable = MappedTables.Where(t => t).Select(t => Mathf.Min(4, t.Capacity)).DefaultIfEmpty(0).Max();
            if (largestTable <= 0) return;
            int size = ChooseCustomerGroupSize(MinimumCustomerGroupSize, MaximumCustomerGroupSize, remaining, largestTable);
            if (size <= 0) return;
            var group = new CustomerGroup(nextGroupId++); groups.Add(group);
            for (int i = 0; i < size; i++)
            {
                GameObject visual = CustomerVisualCatalog ? CustomerVisualCatalog.NextVisual(CustomerVisual) : CustomerVisual;
                GameObject g = visual ? Instantiate(visual) : new GameObject();
                g.name = "CustomerGroup_" + group.Id + "_" + i;
                g.transform.position = GetCustomerSpawnPosition(i, size);
                if (CustomerVisualCatalog && CustomerVisualCatalog.CustomerAnimatorController)
                {
                    Animator visualAnimator = g.GetComponentInChildren<Animator>(true);
                    if (visualAnimator)
                    {
                        visualAnimator.runtimeAnimatorController = CustomerVisualCatalog.CustomerAnimatorController;
                        visualAnimator.applyRootMotion = false;
                        var procedural = g.GetComponentInChildren<ProceduralHumanoidLocomotion>(true);
                        if (procedural) procedural.enabled = false;
                    }
                }
                // Pre-create the navigation and status-bubble components BEFORE PolleriaCustomer so
                // their creation never happens inside a nested Awake (AddComponent-in-Awake returns null here).
                if (!g.GetComponent<NavMeshAgent>()) g.AddComponent<NavMeshAgent>();
                var footsteps = g.GetComponent<FootstepAudioEmitter>() ?? g.AddComponent<FootstepAudioEmitter>();
                footsteps.SetSurfaceHint(FootstepSurface.Tile);
                if (!g.GetComponent<CustomerStatusBubble>()) g.AddComponent<CustomerStatusBubble>();
                var c = g.GetComponent<PolleriaCustomer>() ?? g.AddComponent<PolleriaCustomer>();
                c.Group = group; c.GroupId = group.Id; c.SeatIndex = i; c.MemberId = group.Id + ":" + i;
                c.ConfigureStatusSprites(CustomerStatusSprites); group.Customers.Add(c); customers.Add(c);
            }
            TryAssignTable(group);
        }

        public static int ChooseCustomerGroupSize(int minimum, int maximum, int remainingCustomerSlots, int largestTableCapacity)
        {
            int memberLimit = Mathf.Min(4, Mathf.Max(0, Mathf.Min(remainingCustomerSlots, largestTableCapacity)));
            if (memberLimit == 0) return 0;
            int minSize = Mathf.Clamp(minimum, 1, 4);
            int maxSize = Mathf.Clamp(maximum, minSize, 4);
            return Mathf.Min(UnityEngine.Random.Range(minSize, maxSize + 1), memberLimit);
        }

        Vector3 GetCustomerSpawnPosition(int memberIndex, int groupSize)
        {
            Transform entrance = Anchors ? Anchors.Entrance : null;
            Vector3 basePosition = GetEntryPosition();
            Vector3 lateral = entrance ? entrance.right : transform.right;
            lateral.y = 0f;
            if (lateral.sqrMagnitude < .001f) lateral = Vector3.right;
            lateral.Normalize();
            Vector3 inward = entrance ? entrance.forward : transform.forward;
            inward.y = 0f;
            if (inward.sqrMagnitude < .001f) inward = Vector3.forward;
            inward.Normalize();
            // Queue outside the open doorway instead of fanning a large group into its glass stiles.
            float offset = (memberIndex - (groupSize - 1) * .5f) * .12f;
            // Queue members progressively inside the doorway. Placing later members behind the
            // entrance often put them off NavMesh, where the fallback stacked them on the threshold.
            Vector3 candidate = basePosition + inward * (memberIndex * .65f) + lateral * offset;
            return NavMesh.SamplePosition(candidate, out NavMeshHit hit, .55f, NavMesh.AllAreas) ? hit.position : basePosition;
        }
        void TryAssignWaitingGroups() { foreach (var group in groups) if (group != null && !group.Table) TryAssignTable(group); }
        void TryAssignTable(CustomerGroup group)
        {
            if (group.Table) return;
            for (int i = 0; i < MappedTables.Length; i++)
            {
                var table = MappedTables[i]; if (!table || table.State != TableState.Available || table.Capacity < group.Customers.Count) continue;
                table.Reserve(group.Id); group.Table = table; group.TableIndex = i;
                for (int s = 0; s < group.Customers.Count; s++) { group.Customers[s].AssignedTable = i; group.Customers[s].AssignedSeat = table.SeatAnchors[s]; }
                group.Stage = GroupStage.WaitingForOrder; return;
            }
        }
        bool AllAtSeats(CustomerGroup group) { return group.Customers.All(c => c && c.AssignedSeat && Vector3.Distance(c.transform.position, c.AssignedSeat.position) < .18f); }
        string TakeOrderAtTable(int tableIndex, string workerId)
        {
            var group = groups.FirstOrDefault(g => g.TableIndex == tableIndex && g.Table != null && g.Stage == GroupStage.WaitingForOrder);
            if (group == null || !AllAtSeats(group)) return "El grupo aún no está listo en la mesa.";
            if (!OwnsReservation(PolleriaWorkType.TakeOrder, group.Id.ToString(), workerId)) return "La atención de esta mesa está reservada por otro trabajador.";
            if (group.Order != null) return "Este pedido ya fue tomado.";
            Product product = State.Chicken > 0 && State.Sides > 0 ? Product.PolloConPapa : State.Chicken > 0 ? Product.PolloSimple : Product.Gaseosa;
            group.Order = new Order { Id = State.NextOrder++, Product = product, State = OrderState.WaitingForRegistration, Created = Time.time, Group = group, OriginGroupId = group.Id, OriginTableId = group.Table != null ? group.Table.TableId : string.Empty, ServingCount = Mathf.Max(1, group.GroupSize) };
            State.Orders.Add(group.Order); group.Stage = GroupStage.WaitingForFood;
            if (workerId == "player") manualOrderRegistrationId = group.Order.Id;
            foreach (var customer in group.Customers) { customer.OrderTaken = true; customer.Stage = PolleriaCustomer.CustomerStage.Wait; }
            return "Pedido #" + group.Order.Id + " tomado en mesa " + (tableIndex + 1) + ".";
        }

        public bool TryTakeOrderAtTable(int tableIndex, string workerId)
        {
            if (tableIndex < 0 || tableIndex >= MappedTables.Length || string.IsNullOrEmpty(workerId)) return false;
            CustomerGroup group = GetActiveGroupForTable(tableIndex);
            if (group == null || group.Stage != GroupStage.WaitingForOrder) return false;
            bool alreadyOwned = OwnsReservation(PolleriaWorkType.TakeOrder, group.Id.ToString(), workerId);
            if (!alreadyOwned && !TryReserveWork(PolleriaWorkType.TakeOrder, group.Id.ToString(), workerId)) return false;
            string result = TakeOrderAtTable(tableIndex, workerId);
            bool success = group.Order != null && group.Order.OriginGroupId == group.Id;
            if (!alreadyOwned || !success) ReleaseWork(PolleriaWorkType.TakeOrder, group.Id.ToString(), workerId);
            return success;
        }

        public bool RegisterOrder(Order order, string workerId)
        {
            if (order == null || order.State != OrderState.WaitingForRegistration || string.IsNullOrEmpty(workerId)
                || !OwnsReservation(PolleriaWorkType.RegisterOrder, order.Id.ToString(), workerId)) return false;
            if (order.Group == null || order.Group.Order != order || order.OriginGroupId != order.Group.Id
                || order.Group.Table == null || order.OriginTableId != order.Group.Table.TableId) return false;
            order.State = OrderState.Registered;
            return true;
        }

        public bool TryGetNextOrder(OrderState state, out Order order)
        {
            for (int i = 0; i < State.Orders.Count; i++)
            {
                Order candidate = State.Orders[i];
                if (candidate != null && candidate.State == state) { order = candidate; return true; }
            }
            order = null;
            return false;
        }

        public CustomerGroup GetActiveGroupForTable(int tableIndex)
        {
            if (tableIndex < 0 || tableIndex >= MappedTables.Length) return null;
            for (int i = 0; i < groups.Count; i++)
            {
                CustomerGroup group = groups[i];
                if (group != null && group.TableIndex == tableIndex && group.Table && group.Stage != GroupStage.Exit) return group;
            }
            return null;
        }
        public CustomerGroup GetGroupById(int groupId)
        {
            for (int i = 0; i < groups.Count; i++) if (groups[i] != null && groups[i].Id == groupId) return groups[i];
            return null;
        }
        public Order FindOrder(int orderId)
        {
            for (int i = 0; i < State.Orders.Count; i++) if (State.Orders[i] != null && State.Orders[i].Id == orderId) return State.Orders[i];
            return null;
        }
        public PolleriaPlatePresentation GetOrderPlate(int orderId)
            => orderPlates.TryGetValue(orderId, out PolleriaPlatePresentation plate) && plate ? plate : null;

        public bool TryReserveWork(PolleriaWorkType workType, string targetEntityId, string workerId)
        {
            if (string.IsNullOrEmpty(targetEntityId) || string.IsNullOrEmpty(workerId) || !CanReserve(workType, targetEntityId)) return false;
            if (workType == PolleriaWorkType.DeliverOrder && FindOrder(int.Parse(targetEntityId))?.CarriedByWorkerId != workerId) return false;
            string key = WorkKey(workType, targetEntityId);
            if (workReservations.ContainsKey(key)) return false;
            if (workType == PolleriaWorkType.KitchenDropoff)
            {
                int slot = FindFreeCounterSlot();
                if (slot < 0) return false;
                counterSlotsByOrder[int.Parse(targetEntityId)] = slot;
            }
            workReservations.Add(key, workerId);
            if (workType == PolleriaWorkType.CashierPayment && int.TryParse(targetEntityId, out int paymentGroupId))
            {
                CustomerGroup group = GetGroupById(paymentGroupId);
                if (group?.Order != null) group.Order.State = OrderState.PaymentInProgress;
            }
            return true;
        }
        public bool IsWorkReserved(PolleriaWorkType workType, string targetEntityId) => workReservations.ContainsKey(WorkKey(workType, targetEntityId));
        public bool OwnsReservation(PolleriaWorkType workType, string targetEntityId, string workerId)
            => workReservations.TryGetValue(WorkKey(workType, targetEntityId), out string owner) && owner == workerId;
        public void ReleaseWork(PolleriaWorkType workType, string targetEntityId, string workerId)
        {
            string key = WorkKey(workType, targetEntityId);
            if (workReservations.TryGetValue(key, out string owner) && owner == workerId)
            {
                workReservations.Remove(key);
                if (workType == PolleriaWorkType.KitchenDropoff && int.TryParse(targetEntityId, out int dropoffOrderId))
                {
                    Order dropoffOrder = FindOrder(dropoffOrderId);
                    if (dropoffOrder != null && dropoffOrder.State == OrderState.ReadyToDeliverToCounter) counterSlotsByOrder.Remove(dropoffOrderId);
                }
                if (workType == PolleriaWorkType.CashierPayment && int.TryParse(targetEntityId, out int paymentGroupId))
                {
                    CustomerGroup group = GetGroupById(paymentGroupId);
                    if (group != null && !group.PaymentCompleted && group.Order != null && group.Order.State == OrderState.PaymentInProgress)
                        group.Order.State = OrderState.WaitingToPay;
                }
            }
        }
        static string WorkKey(PolleriaWorkType type, string target) => ((int)type) + ":" + target;

        bool CanReserve(PolleriaWorkType type, string target)
        {
            if (!int.TryParse(target, out int id)) return false;
            switch (type)
            {
                case PolleriaWorkType.TakeOrder:
                    CustomerGroup group = GetGroupById(id);
                    return group != null && group.Stage == GroupStage.WaitingForOrder && AllAtSeats(group);
                case PolleriaWorkType.RegisterOrder: return FindOrder(id)?.State == OrderState.WaitingForRegistration;
                case PolleriaWorkType.CookOrder: return FindOrder(id)?.State == OrderState.Registered;
                case PolleriaWorkType.KitchenDropoff: return FindOrder(id)?.State == OrderState.ReadyToDeliverToCounter && FindFreeCounterSlot() >= 0;
                case PolleriaWorkType.CounterPickup: return FindOrder(id)?.State == OrderState.ReadyToPickup
                    && State.CarriedOrder == null && !State.Orders.Any(o => o != null && o.State == OrderState.CarriedToTable);
                case PolleriaWorkType.DeliverOrder:
                    Order delivery = FindOrder(id);
                    return delivery != null && delivery.State == OrderState.CarriedToTable && delivery.Group != null && delivery.Group.Stage == GroupStage.ReadyForDelivery
                        && workReservations.ContainsKey(WorkKey(type, target)) == false;
                case PolleriaWorkType.CashierPayment:
                    CustomerGroup paying = GetGroupById(id);
                    return paying != null && !paying.PaymentCompleted && paying.Stage == GroupStage.Payment && PayingCustomerAtCashier(paying)
                        && (paying.Order == null || paying.Order.State == OrderState.WaitingToPay);
                default: return false;
            }
        }
        string CookBatch()
        {
            if (!TryReserveCookingOrder("player", out var order))
                return !State.GrillPrepared ? "Prepara la brasa primero." : "No hay pedido pendiente para preparar o la brasa está ocupada.";
            if (!CompleteReservedCooking(order, "player")) return "No se pudo completar la preparación.";
            audioController?.PlayGrillUse();
            return "Pedido #" + order.Id + " listo. Llévalo a la mesa " + order.OriginTableId + ".";
        }

        /// <summary>Shared authoritative cooking reservation used by player and the D9 Grill employee.</summary>
        public bool TryReserveCookingOrder(string workerId, out Order order)
        {
            order = null;
            if (!State.GrillPrepared || string.IsNullOrEmpty(workerId)) return false;
            if (State.Orders.Any(o => o != null && o.State == OrderState.Preparing && !string.IsNullOrEmpty(o.CookingReservationId))) return false;
            order = State.Orders.FirstOrDefault(o => o != null && o.State == OrderState.Registered && HasIngredients(o));
            if (order == null) return false;
            if (!TryReserveWork(PolleriaWorkType.CookOrder, order.Id.ToString(), workerId)) { order = null; return false; }
            order.State = OrderState.Preparing;
            order.CookingReservationId = workerId;
            return true;
        }

        /// <summary>Completes exactly one reserved cook and consumes ingredients exactly once.</summary>
        public bool CompleteReservedCooking(Order order, string workerId)
        {
            if (order == null || order.State != OrderState.Preparing || order.CookingReservationId != workerId) return false;
            bool playerCooked = string.Equals(workerId, "player", StringComparison.Ordinal);
            if (playerCooked && (order.Group == null || order.Group.Order != order || order.Group.Id != order.OriginGroupId
                || order.Group.Table == null || order.Group.Table.TableId != order.OriginTableId))
            {
                CancelCookingReservation(order, workerId);
                return false;
            }
            if (!HasIngredients(order)) { order.State = OrderState.Registered; order.CookingReservationId = null; ReleaseWork(PolleriaWorkType.CookOrder, order.Id.ToString(), workerId); return false; }
            var consumeResult = InventoryState != null ? InventoryState.TryConsume(CostForOrder(order), "order:" + order.Id) : default;
            if (InventoryState != null && !consumeResult.Applied) { order.State = OrderState.Registered; order.CookingReservationId = null; ReleaseWork(PolleriaWorkType.CookOrder, order.Id.ToString(), workerId); return false; }
            if (InventoryState == null) ConsumeIngredients(order);
            else SyncDayStockFromInventory();
            order.State = playerCooked ? OrderState.CarriedToTable : OrderState.ReadyToDeliverToCounter;
            order.CookingReservationId = null;
            order.Quality = State.Cleanliness > 50 ? 1 : .7f;
            if (playerCooked)
            {
                order.CarriedByWorkerId = workerId;
                order.Group.Stage = GroupStage.ReadyForDelivery;
                foreach (var customer in order.Group.Customers) customer.Stage = PolleriaCustomer.CustomerStage.Receive;
                State.CarriedOrder = order;
            }
            ReleaseWork(PolleriaWorkType.CookOrder, order.Id.ToString(), workerId);
            if (!orderPlates.TryGetValue(order.Id, out var plate) || !plate)
            {
                plate = PolleriaPlatePresentation.Create(order.Id, order.OriginTableId, order.ServingCount);
                orderPlates[order.Id] = plate;
            }
            Transform grill = GetGrillWorkAnchor();
            if (playerCooked)
            {
                var player = FindFirstObjectByType<PlayerSession>();
                var visual = player ? player.GetComponentInChildren<PlayerVisual>() : null;
                var animator = visual && visual.Current ? visual.Current.GetComponentInChildren<Animator>() : null;
                Transform hand = animator ? animator.GetBoneTransform(HumanBodyBones.RightHand) : null;
                if (hand)
                {
                    plate.AttachForCarry(hand, new Vector3(.045f, .035f, .09f), Quaternion.identity);
                }
                else if (player) plate.transform.position = player.transform.position + player.transform.forward * .35f + Vector3.up * 1.05f;
                RefreshTableMarkers();
            }
            else if (grill) plate.transform.position = grill.position + Vector3.up * .95f;
            return true;
        }

        public void CancelCookingReservation(Order order, string workerId)
        {
            if (order != null && order.State == OrderState.Preparing && order.CookingReservationId == workerId)
            { order.State = OrderState.Registered; order.CookingReservationId = null; ReleaseWork(PolleriaWorkType.CookOrder, order.Id.ToString(), workerId); }
        }
        bool HasIngredients(Order order)
        {
            if (order == null) return false;
            StockCost required = CostForOrder(order);
            return InventoryState != null ? InventoryState.CanConsume(required)
                : State.Chicken >= required.Chicken && State.Sides >= required.Sides && State.Drinks >= required.Drinks;
        }
        void ConsumeIngredients(Order order)
        {
            switch (order.Product)
            {
                case Product.PolloConPapa: State.Chicken -= order.ServingCount; State.Sides -= order.ServingCount; break;
                case Product.PolloSimple: State.Chicken -= order.ServingCount; break;
                case Product.Gaseosa: State.Drinks -= order.ServingCount; break;
            }
            State.Chicken = Mathf.Max(0, State.Chicken);
            State.Sides = Mathf.Max(0, State.Sides);
            State.Drinks = Mathf.Max(0, State.Drinks);
        }
        string DeliverPreparedOrderToCounter()
        {
            var order = State.Orders.FirstOrDefault(o => o.State == OrderState.ReadyToDeliverToCounter);
            if (order == null) return "No hay pedido preparado para entregar al mostrador.";
            if (!TryReserveWork(PolleriaWorkType.KitchenDropoff, order.Id.ToString(), "player")) return "Otro trabajador lleva este pedido al mostrador.";
            bool success = TryDropoffPreparedOrder(order.Id, "player");
            ReleaseWork(PolleriaWorkType.KitchenDropoff, order.Id.ToString(), "player");
            return success ? "Pedido #" + order.Id + " entregado al mostrador. Ya puede recogerse por delante." : "No se pudo entregar el pedido.";
        }

        public bool TryDropoffPreparedOrder(int orderId, string workerId)
        {
            Order order = FindOrder(orderId);
            if (order == null || order.State != OrderState.ReadyToDeliverToCounter || !OwnsReservation(PolleriaWorkType.KitchenDropoff, orderId.ToString(), workerId)) return false;
            if (!counterSlotsByOrder.TryGetValue(order.Id, out int slot) || slot >= CounterReadyOrderCapacity || slot >= counterPlateSlots.Length) return false;
            order.State = OrderState.ReadyToPickup;
            if (orderPlates.TryGetValue(order.Id, out var plate) && plate && counterPlateSlots[slot])
            {
                plate.transform.SetParent(counterPlateSlots[slot], false);
                plate.transform.localPosition = Vector3.zero;
                plate.transform.localRotation = Quaternion.identity;
                plate.transform.localScale = Vector3.one;
                plate.SetCounterSlot(slot);
            }
            return true;
        }

        public int GetCounterReadyCount() => CounterReadyOrderCount();
        public int GetCounterReadyCapacity() => CounterReadyOrderCapacity;
        public Order GetNextOrder(OrderState state)
        {
            for (int i = 0; i < State.Orders.Count; i++) if (State.Orders[i] != null && State.Orders[i].State == state) return State.Orders[i];
            return null;
        }
        public Transform GetCounterPlateSlot(int slot) => slot >= 0 && slot < counterPlateSlots.Length ? counterPlateSlots[slot] : null;
        public Transform GetOrderTableApproach(Order order) => order != null && order.Group != null && order.Group.TableIndex >= 0
            && order.Group.TableIndex < MappedTables.Length && MappedTables[order.Group.TableIndex] ? MappedTables[order.Group.TableIndex].DeliveryInteraction.transform : null;
        public Transform GetOrderStationEmployeeAnchor() => Anchors ? Anchors.OrderStationEmployee : null;
        public Transform GetWaiterIdleAnchor() => Anchors ? Anchors.WaiterIdle : null;
        public Transform GetChefIdleAnchor() => Anchors ? Anchors.ChefIdle : null;
        public Transform GetCashierIdleAnchor() => Anchors ? Anchors.CashierIdle : null;
        public Transform GetEmployeeEntranceAnchor() => Anchors ? Anchors.EmployeeEntrance : null;
        public Transform GetEmployeeExitAnchor() => Anchors ? Anchors.EmployeeExit : null;
        public Transform GetKitchenDropoffEmployeeAnchor() => Anchors ? Anchors.KitchenDropoffEmployee : CounterKitchenDropoffAnchor;
        public Transform GetCustomerPickupEmployeeAnchor() => Anchors ? Anchors.CustomerPickupEmployee : CounterCustomerPickupAnchor;
        public Transform GetCashierWorkAnchor() => Anchors ? Anchors.CashierWork : Anchors ? Anchors.Cashier : null;
        public Transform GetOrderStationAnchor() => Anchors ? Anchors.OrderStationEmployee : null;
        public Transform GetRoleIdleAnchor(EmployeeRole role) => role == EmployeeRole.Waiter ? GetWaiterIdleAnchor()
            : role == EmployeeRole.Chef ? GetChefIdleAnchor() : GetCashierIdleAnchor();

        int CounterReadyOrderCapacity
        {
            get
            {
                var upgrades = ManagementCompositionRoot.Instance != null ? ManagementCompositionRoot.Instance.Upgrades : null;
                return upgrades != null ? Mathf.Max(1, upgrades.ReadModel.CounterReadyOrderCapacity) : 1;
            }
        }

        int CounterReadyOrderCount() => State.Orders.Count(o => o.State == OrderState.ReadyToPickup);
        int FindFreeCounterSlot()
        {
            int limit = Mathf.Min(CounterReadyOrderCapacity, counterPlateSlots.Length);
            for (int slot = 0; slot < limit; slot++) if (!counterSlotsByOrder.Values.Contains(slot)) return slot;
            return -1;
        }
        string PickupReadyOrder()
        {
            var order = State.Orders.FirstOrDefault(o => o.State == OrderState.ReadyToPickup);
            if (order == null) return "No hay pedido listo en el mostrador.";
            if (!TryReserveWork(PolleriaWorkType.CounterPickup, order.Id.ToString(), "player")) return "Otro trabajador está recogiendo este pedido.";
            bool picked = TryPickupReadyOrder(order.Id, "player");
            ReleaseWork(PolleriaWorkType.CounterPickup, order.Id.ToString(), "player");
            return picked ? "Pedido #" + order.Id + " recogido. Llévalo a la mesa marcada." : "No se pudo recoger el pedido.";
        }
        public bool TryPickupReadyOrder(int orderId, string workerId)
        {
            Order order = FindOrder(orderId);
            if (order == null || order.State != OrderState.ReadyToPickup || string.IsNullOrEmpty(workerId)
                || !OwnsReservation(PolleriaWorkType.CounterPickup, orderId.ToString(), workerId)) return false;
            order.State = OrderState.CarriedToTable;
            order.CarriedByWorkerId = workerId;
            counterSlotsByOrder.Remove(order.Id);
            if (workerId == "player") State.CarriedOrder = order;
            order.Group.Stage = GroupStage.ReadyForDelivery;
            foreach (var customer in order.Group.Customers) customer.Stage = PolleriaCustomer.CustomerStage.Receive;
            return true;
        }

        string DeliverFoodToTable(int tableIndex)
        {
            CustomerGroup group = GetActiveGroupForTable(tableIndex);
            Order order = group != null ? group.Order : null;
            if (order == null || !TryReserveWork(PolleriaWorkType.DeliverOrder, order.Id.ToString(), "player")) return "No llevas el pedido de esta mesa.";
            bool delivered = TryDeliverOrderToTable(tableIndex, order.Id, "player");
            ReleaseWork(PolleriaWorkType.DeliverOrder, order.Id.ToString(), "player");
            RefreshTableMarkers();
            return delivered ? "Pedido entregado en la mesa " + (tableIndex + 1) + ". Comiendo durante " + EatingDurationSeconds.ToString("0") + " s." : "El pedido no corresponde a esta mesa.";
        }
        public bool TryDeliverOrderToTable(int tableIndex, int orderId, string workerId)
        {
            if (tableIndex < 0 || tableIndex >= MappedTables.Length || string.IsNullOrEmpty(workerId)) return false;
            Order order = FindOrder(orderId);
            if (order == null || order.CarriedByWorkerId != workerId || order.State != OrderState.CarriedToTable
                || !OwnsReservation(PolleriaWorkType.DeliverOrder, orderId.ToString(), workerId)) return false;
            CustomerGroup group = GetActiveGroupForTable(tableIndex);
            if (group == null || group.Order != order || group.Id != order.OriginGroupId || group.Table == null
                || group.Table.TableId != order.OriginTableId) return false;
            return CompleteFoodDelivery(tableIndex, workerId);
        }

        bool CompleteFoodDelivery(int tableIndex, string workerId)
        {
            if (tableIndex < 0 || tableIndex >= MappedTables.Length) return false;
            var group = groups.FirstOrDefault(g => g.TableIndex == tableIndex && g.Stage == GroupStage.ReadyForDelivery);
            if (group == null || group.Order == null || group.Table == null || group.Order.OriginTableId != group.Table.TableId || group.Order.OriginGroupId != group.Id) return false;
            if (group.Order.CarriedByWorkerId != workerId || group.Order.State != OrderState.CarriedToTable
                || !OwnsReservation(PolleriaWorkType.DeliverOrder, group.Order.Id.ToString(), workerId)) return false;
            group.Order.Delivered = true; group.Order.State = OrderState.DeliveredToTable; group.Order.CarriedByWorkerId = null;
            if (workerId == "player") State.CarriedOrder = null;
            if (orderPlates.TryGetValue(group.Order.Id, out var plate) && plate) { Destroy(plate.gameObject); orderPlates.Remove(group.Order.Id); }
            group.Stage = GroupStage.Eating; group.EatingElapsed = 0;
            foreach (var customer in group.Customers) customer.Stage = PolleriaCustomer.CustomerStage.Consume;
            return true;
        }
        void BeginPayment(CustomerGroup group)
        {
            if (group.Stage != GroupStage.Eating || group.Customers.Count == 0) return;
            if (group.Order != null) group.Order.State = OrderState.WaitingToPay;
            group.PayingCustomer = group.Customers[UnityEngine.Random.Range(0, group.Customers.Count)];
            group.Stage = GroupStage.Payment;
            foreach (var customer in group.Customers)
                customer.Stage = customer == group.PayingCustomer ? PolleriaCustomer.CustomerStage.Payment : PolleriaCustomer.CustomerStage.Consume;
        }

        void RebuildPaymentQueue()
        {
            var paying = groups.Where(g => g != null && g.Stage == GroupStage.Payment && g.PayingCustomer).OrderBy(g => g.Id).ToList();
            for (int i = 0; i < paying.Count; i++) paying[i].PayingCustomer.QueueIndex = i;
        }

        internal Vector3 GetPaymentQueuePosition(PolleriaCustomer customer)
        {
            if (QueuePositions != null && QueuePositions.Length > 0)
            {
                int index = Mathf.Max(0, customer ? customer.QueueIndex : 0);
                int last = QueuePositions.Length - 1;
                if (index <= last && QueuePositions[index]) return QueuePositions[index].position;
                if (QueuePositions[last])
                {
                    Vector3 direction = QueuePositions.Length > 1 && QueuePositions[last - 1]
                        ? QueuePositions[last].position - QueuePositions[last - 1].position
                        : (Anchors ? Anchors.transform.right : transform.right);
                    if (direction.sqrMagnitude < .001f) direction = Vector3.right;
                    return QueuePositions[last].position + direction.normalized * .8f * (index - last);
                }
            }
            return GetCashierPosition();
        }

        bool PayingCustomerAtCashier(CustomerGroup group)
        {
            if (!group.PayingCustomer) return false;
            Vector3 target = GetPaymentQueuePosition(group.PayingCustomer);
            bool atSlot = (group.PayingCustomer.transform.position - target).sqrMagnitude < .32f * .32f;
            bool front = group.PayingCustomer.QueueIndex == 0;
            return front && atSlot;
        }
        bool HasWaitingPlayerPayment()
        {
            for (int i = 0; i < groups.Count; i++)
            {
                CustomerGroup group = groups[i];
                if (group != null && group.Stage == GroupStage.Payment && !group.PaymentCompleted && PayingCustomerAtCashier(group)
                    && !IsWorkReserved(PolleriaWorkType.CashierPayment, group.Id.ToString())) return true;
            }
            return false;
        }
        bool HasAnyCashierReservation()
        {
            for (int i = 0; i < groups.Count; i++)
                if (groups[i] != null && IsWorkReserved(PolleriaWorkType.CashierPayment, groups[i].Id.ToString())) return true;
            return false;
        }
        string ServeFrontPayerManually()
        {
            CustomerGroup group = groups.FirstOrDefault(g => g != null && g.Stage == GroupStage.Payment && !g.PaymentCompleted
                && PayingCustomerAtCashier(g));
            if (group == null || !TryReserveWork(PolleriaWorkType.CashierPayment, group.Id.ToString(), "player"))
                return "No hay un cliente listo para pagar en caja.";
            string result = CompletePayment(group.Id, "player");
            ReleaseWork(PolleriaWorkType.CashierPayment, group.Id.ToString(), "player");
            RefreshTableMarkers();
            return result;
        }

        public CustomerGroup GetFrontPayerGroup()
        {
            RebuildPaymentQueue();
            return groups.FirstOrDefault(g => g != null && g.Stage == GroupStage.Payment && !g.PaymentCompleted
                && g.PayingCustomer && g.PayingCustomer.QueueIndex == 0);
        }
        public bool IsPayerAtCashier(CustomerGroup group) => group != null && PayingCustomerAtCashier(group);
        public bool TryCompleteEmployeePayment(int groupId, string employeeId)
        {
            if (string.IsNullOrEmpty(employeeId) || !OwnsReservation(PolleriaWorkType.CashierPayment, groupId.ToString(), employeeId)) return false;
            return CompletePayment(groupId, employeeId).StartsWith("Pago completado", StringComparison.Ordinal);
        }
        string CompletePayment(int groupId, string workerId)
        {
            CustomerGroup group = GetGroupById(groupId);
            if (group == null || group.Stage != GroupStage.Payment || group.PaymentCompleted || !PayingCustomerAtCashier(group)
                || !OwnsReservation(PolleriaWorkType.CashierPayment, groupId.ToString(), workerId)) return "El cliente aún no está listo para cobrar.";
            // Management: money ownership lives in EconomyService. Polleria routes the sale through the payment handler.
            string orderId = group.Order != null ? "order:" + group.Order.Id : "group:" + group.Id;
            var root = FinDeMes.Management.ManagementCompositionRoot.Instance;
            long amountMinor = group.Order != null ? GetOrderTotalMinor(group.Order) : PolleriaSalePriceMinor;
            if (root != null && root.PaymentHandler != null && group.Order != null)
            {
                var cmd = new FinDeMes.Management.RecordPolleriaSaleCommand(orderId, group.Id, amountMinor, 1);
                var result = root.PaymentHandler.Handle(cmd);
                if (!result.Accepted) return "El pago ya fue registrado o fue rechazado."; // duplicate or rejected; do not complete the group
                State.Sales = State.Sales + amountMinor; // non-authoritative session projection of successful sales
            }
            else
            {
                // Composition root absent (e.g. test scenes without bootstrap). Keep the gameplay flow alive but do not create authoritative money.
                State.Sales = State.Sales + amountMinor;
            }
            group.PaymentCompleted = true;
            if (group.Order != null) group.Order.PaymentCompleted = true;
            if (group.Order != null) { group.Order.State = OrderState.Paid; group.Order.Delivered = true; }
            State.Served += group.GroupSize; State.Cleanliness = Mathf.Max(0, State.Cleanliness - 2); group.Stage = GroupStage.Exit;
            if (group.Order != null) group.Order.State = OrderState.Completed;
            foreach (var customer in group.Customers) customer.Stage = PolleriaCustomer.CustomerStage.Exit;
            audioController?.PlayCashRegisterUse();
            return "Pago completado: S/ " + (amountMinor / 100f).ToString("0.00") + ". El cliente se retira.";
        }

        public long GetPriceMinor(Product product)
        {
            if (Prices == null) Prices = new MenuPrices();
            return product switch
            {
                Product.PolloSimple => Math.Max(0, Prices.PolloSimpleMinor),
                Product.PolloConPapa => Math.Max(0, Prices.PolloConPapaMinor),
                Product.Gaseosa => Math.Max(0, Prices.GaseosaMinor),
                _ => 0
            };
        }

        public long GetOrderTotalMinor(Order order)
            => order == null ? 0L : GetPriceMinor(order.Product) * Mathf.Max(1, order.ServingCount);

        public float GetWaiterTakeOrderDuration(CustomerGroup group)
            => Mathf.Max(.1f, EmployeeTimings.WaiterTakeOrderSeconds
                + Mathf.Max(0, (group != null ? group.GroupSize : 1) - 1) * EmployeeTimings.WaiterTakeOrderAdditionalSecondsPerServing);

        public float GetChefCookingDuration(Order order)
            => Mathf.Max(.1f, EmployeeTimings.ChefCookingSeconds
                + Mathf.Max(0, (order != null ? order.ServingCount : 1) - 1) * EmployeeTimings.ChefAdditionalSecondsPerServing);

        /// <summary>Fallback sale price for groups without a valid product order.</summary>
        public const long PolleriaSalePriceMinor = 18L;
        string CleanTable(int tableIndex)
        {
            if (tableIndex < 0 || tableIndex >= MappedTables.Length || MappedTables[tableIndex].State != TableState.NeedsCleaning) return "Esta mesa no necesita limpieza.";
            MappedTables[tableIndex].Clean();
            if (tableIndex < State.Tables.Length) State.Tables[tableIndex] = TableState.Available;
            State.Cleanliness = Mathf.Min(100, State.Cleanliness + 15);
            return "Mesa " + (tableIndex + 1) + " limpia y disponible.";
        }

        void DisableLegacyStation(string objectName)
        {
            var stations = FindObjectsByType<ContextInteraction>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var station in stations) if (station && station.name == objectName) station.gameObject.SetActive(false);
        }
        string CloseBusiness()
        {
            if (State.Business != BusinessState.Open) return "La pollería ya está cerrada."; State.Business = BusinessState.Closing; State.Lost += customers.Count(c => c && c.Group != null && c.Group.Stage != GroupStage.Exit);
            foreach (var c in customers) if (c) Destroy(c.gameObject); customers.Clear(); groups.Clear(); ClearEmployeeWork(); State.Business = BusinessState.ClosedForDay; return Results();
        }
        string Results() => "DÍA CERRADO · Servidos: " + State.Served + " · Perdidos: " + State.Lost + " · Ventas: S/ " + State.Sales.ToString("0") + " · Costes: S/ " + State.Costs.ToString("0") + " · Neto: S/ " + State.Net.ToString("0") + " · Limpieza: " + State.Cleanliness.ToString("0");
        void ClearEmployeeWork()
        {
            workReservations.Clear();
            counterSlotsByOrder.Clear();
            manualOrderRegistrationId = -1;
            State.CarriedOrder = null;
            foreach (var plate in orderPlates.Values) if (plate) Destroy(plate.gameObject);
            orderPlates.Clear();
        }
        public void ResetDay() { foreach (var c in customers) if (c) Destroy(c.gameObject); customers.Clear(); groups.Clear(); ClearEmployeeWork(); State = new DayState(); nextCustomer = 0; nextGroupId = 1; foreach (var table in MappedTables) if (table) table.Clean(); ResetTableStateArray(); }
        internal Vector3 GetEntryPosition() => Anchors && Anchors.Entrance ? Anchors.Entrance.position : transform.position;
        internal Vector3 GetExitPosition(PolleriaCustomer customer)
        {
            Transform exit = Anchors ? Anchors.Entrance : null;
            Vector3 right = exit ? exit.right : transform.right;
            right.y = 0f;
            if (right.sqrMagnitude < .001f) right = Vector3.right;
            right.Normalize();
            int count = customer && customer.Group != null ? customer.Group.GroupSize : 1;
            float lateralOffset = (customer ? customer.SeatIndex : 0) - (count - 1) * .5f;
            return GetEntryPosition() + right * lateralOffset * .55f;
        }
        internal Vector3 GetEntryPositionForEmployee() => Anchors && Anchors.ServiceEntrance ? Anchors.ServiceEntrance.position : GetEntryPosition();
        internal Transform GetGrillWorkAnchor() => Anchors ? Anchors.Grill : null;
        internal Vector3 GetCashierPosition() => Anchors && Anchors.Cashier ? Anchors.Cashier.position : transform.position;
        internal Vector3 GetTableCenter(PolleriaCustomer customer) => customer && customer.AssignedTable >= 0 && customer.AssignedTable < MappedTables.Length && MappedTables[customer.AssignedTable].VisualTable ? MappedTables[customer.AssignedTable].VisualTable.position : customer.transform.position;
    }

    public sealed class PolleriaCustomer : MonoBehaviour
    {
        public enum CustomerStage { Enter, Queue, ReadyToOrder, Wait, Receive, Consume, Payment, Exit }
        public enum Mood { Happy, Waiting, Impatient, Angry, Disgusted }
        public CustomerStage Stage { get; internal set; } = CustomerStage.Enter;
        public Mood CurrentMood { get; internal set; } = Mood.Happy;
        public bool OrderTaken, Served; public int QueueIndex, AssignedTable = -1, GroupId, SeatIndex; public string MemberId { get; internal set; } public Transform AssignedSeat;
        public bool IsSeated => seating != null && seating.IsSeatedAt(AssignedSeat);
        public PolleriaTier0.CustomerGroup Group { get; internal set; }
        ProceduralHumanoidLocomotion locomotion; Animator customerAnimator; PlayerMotor playerMotor; PlayerAnimationController playerAnimation; CustomerStatusBubble statusBubble;
        NavMeshAgent agent; PolleriaCustomerSeatState seating; PolleriaHumanoidSeatPose seatedPose; bool navmeshAvailable, pathWarningLogged;
        static readonly int SpeedParam = Animator.StringToHash("Speed");
        static readonly int SeatedParam = Animator.StringToHash("IsSeated");
        bool supportsSeatedAnimation, lastSeatedAnimation;
        float orderWaitElapsed, walkParam = .4773f;
        Vector3 previousPosition;
#if UNITY_EDITOR
        /// <summary>Development-only direct movement used to keep editor proofs running when no NavMesh exists; production builds never use it.</summary>
        public static bool DebugDirectMovement;
#endif

        void Awake()
        {
            locomotion = GetComponentInChildren<ProceduralHumanoidLocomotion>(true);
            var animator = GetComponentInChildren<Animator>(true);
            customerAnimator = animator;
            playerMotor = FindAnyObjectByType<PlayerMotor>();
            playerAnimation = FindAnyObjectByType<PlayerAnimationController>();
            if (animator && !animator.runtimeAnimatorController && playerAnimation && playerAnimation.VisualAnimator)
                animator.runtimeAnimatorController = playerAnimation.VisualAnimator.runtimeAnimatorController;
            if (animator) animator.applyRootMotion = false;
            if (animator && animator.runtimeAnimatorController && locomotion) locomotion.enabled = false;
            supportsSeatedAnimation = HasSeatedAnimationParameter(animator);
            if (!supportsSeatedAnimation && animator && animator.isHuman && animator.avatar && animator.avatar.isValid)
                seatedPose = new PolleriaHumanoidSeatPose(animator);
            if (animator && playerMotor) walkParam = playerMotor.WalkSpeed / playerMotor.RunSpeed;
            if (!locomotion && animator && animator.isHuman && animator.avatar && animator.avatar.isValid && !animator.runtimeAnimatorController)
                locomotion = animator.gameObject.AddComponent<ProceduralHumanoidLocomotion>();
            agent = GetComponent<NavMeshAgent>();
            if (!agent) agent = gameObject.AddComponent<NavMeshAgent>();
            if (agent)
            {
                agent.radius = .3f; agent.height = 1.75f; agent.speed = 1.35f; agent.angularSpeed = 540f;
                agent.acceleration = 4f; agent.stoppingDistance = .12f; agent.autoBraking = true;
                agent.updateRotation = true; agent.updatePosition = true; agent.isStopped = false;
                EnsureAgentOnNavmesh();
            }
            seating = new PolleriaCustomerSeatState(transform, agent);
            statusBubble = GetComponent<CustomerStatusBubble>();
            if (!statusBubble) statusBubble = gameObject.AddComponent<CustomerStatusBubble>();
            previousPosition = transform.position;
            navmeshAvailable = agent && NavMesh.SamplePosition(transform.position, out _, 2f, WalkableMask);
            if (agent) agent.areaMask = WalkableMask;
            if (agent) agent.enabled = navmeshAvailable;
            if (!navmeshAvailable)
                Debug.LogWarning("PolleriaCustomer on " + name + ": no walkable NavMesh at spawn; customer will not move through geometry.", this);
        }
        public void ConfigureStatusSprites(Sprite[] sprites) { if (statusBubble) statusBubble.SetSprites(sprites); }
        public void Tick(PolleriaTier0 business, float deltaTime)
        {
            UpdateMood(business, deltaTime);
            bool exiting = Group != null && Group.Stage == PolleriaTier0.GroupStage.Exit;
            bool isPayer = Group != null && Group.Stage == PolleriaTier0.GroupStage.Payment && this == Group.PayingCustomer;
            bool wantsSeat = !exiting && !isPayer && AssignedSeat != null && Group != null && Group.Table != null;
            if (!wantsSeat) seating?.Leave();
            if (wantsSeat && seating != null && seating.IsSeatedAt(AssignedSeat))
            {
                FaceTable(business, deltaTime);
                goto Present;
            }
            if (navmeshAvailable && agent)
            {
                if (!agent.enabled) EnableAgent();
                Vector3 destination = wantsSeat ? GetSeatApproach(business) : ClampToNavmesh(ResolveTarget(business));
                if ((agent.destination - destination).sqrMagnitude > .01f)
                {
                    agent.SetDestination(destination);
                    pathWarningLogged = false;
                }
                if (agent.pathStatus == NavMeshPathStatus.PathInvalid && !pathWarningLogged)
                {
                    pathWarningLogged = true;
                    Debug.LogWarning("PolleriaCustomer on " + name + ": no valid NavMesh path to " + destination + "; keeping customer stationary.", this);
                }
                if (Arrived())
                {
                    if (exiting) { Destroy(gameObject); return; }
                    if (wantsSeat)
                    {
                        SeatAtAssignedAnchor();
                        FaceTable(business, deltaTime);
                    }
                }
            }
#if UNITY_EDITOR
            else if (DebugDirectMovement)
            {
                // Editor-only proof fallback. Never active in production builds.
                Vector3 target = wantsSeat ? AssignedSeat.position : ResolveTarget(business);
                MoveDirect(target, deltaTime);
                if ((transform.position - target).sqrMagnitude < .04f)
                {
                    if (exiting) { Destroy(gameObject); return; }
                    if (wantsSeat)
                    {
                        SeatAtAssignedAnchor();
                        FaceTable(business, deltaTime);
                    }
                }
            }
#endif
        Present:
            FeedLocomotion(deltaTime);
            if (statusBubble) statusBubble.Refresh(this);
        }

        void LateUpdate()
        {
            bool isSeated = IsSeated;
            if (supportsSeatedAnimation && customerAnimator)
            {
                if (isSeated != lastSeatedAnimation)
                {
                    customerAnimator.SetBool(SeatedParam, isSeated);
                    lastSeatedAnimation = isSeated;
                }
                if (isSeated) customerAnimator.SetFloat(SpeedParam, 0f);
                return;
            }

            if (isSeated) seatedPose?.Apply();
        }

        static bool HasSeatedAnimationParameter(Animator animator)
        {
            if (!animator || !animator.runtimeAnimatorController) return false;
            foreach (AnimatorControllerParameter parameter in animator.parameters)
                if (parameter.nameHash == SeatedParam && parameter.type == AnimatorControllerParameterType.Bool)
                    return true;
            return false;
        }

        void OnDestroy()
        {
            seatedPose?.Dispose();
        }

        void MoveDirect(Vector3 target, float deltaTime)
        {
            Vector3 delta = target - transform.position; delta.y = 0;
            transform.position = Vector3.MoveTowards(transform.position, target, deltaTime * 1.35f);
            if (delta.sqrMagnitude > .01f) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(delta.normalized), 1 - Mathf.Exp(-10 * deltaTime));
        }

        Vector3 ResolveTarget(PolleriaTier0 business)
        {
            if (Group == null || !Group.Table) return business.GetEntryPosition();
            if (Group.Stage == PolleriaTier0.GroupStage.Payment && this == Group.PayingCustomer) return business.GetPaymentQueuePosition(this);
            if (Group.Stage == PolleriaTier0.GroupStage.Exit) return business.GetExitPosition(this);
            return AssignedSeat ? AssignedSeat.position : business.GetTableCenter(this);
        }

        Vector3 GetSeatApproach(PolleriaTier0 business)
        {
            if (!AssignedSeat) return ClampToNavmesh(business.GetTableCenter(this));
            Vector3 away = AssignedSeat.position - business.GetTableCenter(this); away.y = 0;
            if (away.sqrMagnitude < .0001f) away = Vector3.forward;
            return ClampToNavmesh(AssignedSeat.position + away.normalized * .45f);
        }

        void SeatAtAssignedAnchor()
        {
            if (seating == null || !seating.Enter(AssignedSeat)) return;
            // Do not interpret the final NavMesh-to-seat alignment as walking motion.
            previousPosition = transform.position;
        }

        static int WalkableMask
        {
            get
            {
                int notWalkable = NavMesh.GetAreaFromName("Not Walkable");
                int mask = NavMesh.AllAreas;
                if (notWalkable >= 0 && notWalkable < 32) mask &= ~(1 << notWalkable);
                int staffOnly = NavMesh.GetAreaFromName("PolleriaStaffOnly");
                if (staffOnly >= 0 && staffOnly < 32) mask &= ~(1 << staffOnly);
                return mask != 0 ? mask : NavMesh.AllAreas;
            }
        }

        static Vector3 ClampToNavmesh(Vector3 target)
        {
            if (NavMesh.SamplePosition(target, out NavMeshHit hit, 1.5f, WalkableMask)) return hit.position;
            return target;
        }

        void EnableAgent()
        {
            agent.enabled = true;
            EnsureAgentOnNavmesh();
        }

        bool Arrived() => !agent.pathPending && agent.remainingDistance <= agent.stoppingDistance + .08f;

        void EnsureAgentOnNavmesh()
        {
            if (!agent || agent.isOnNavMesh) return;
            if (NavMesh.SamplePosition(agent.transform.position, out NavMeshHit hit, 3f, WalkableMask))
                agent.Warp(hit.position);
        }

        void FaceTable(PolleriaTier0 business, float deltaTime)
        {
            Vector3 face = business.GetTableCenter(this) - transform.position; face.y = 0;
            if (face.sqrMagnitude > .01f) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(face.normalized), 1 - Mathf.Exp(-10 * deltaTime));
        }

        void FeedLocomotion(float deltaTime)
        {
            Vector3 velocity = deltaTime > .0001f ? (transform.position - previousPosition) / deltaTime : Vector3.zero;
            previousPosition = transform.position;
            velocity.y = 0;
            if (locomotion && locomotion.isActiveAndEnabled) locomotion.SetExternalMotion(velocity, true);
            else if (customerAnimator && customerAnimator.runtimeAnimatorController)
                customerAnimator.SetFloat(SpeedParam, IsSeated ? 0f : velocity.magnitude > .1f ? walkParam : 0f, .15f, deltaTime);
        }
        void UpdateMood(PolleriaTier0 business, float deltaTime)
        {
            if (Group == null) { CurrentMood = Mood.Happy; return; }
            if (!Group.Table)
            {
                CurrentMood = business.HasDirtyTables ? Mood.Disgusted : Mood.Waiting;
                return;
            }
            if (Stage == CustomerStage.ReadyToOrder)
            {
                orderWaitElapsed += deltaTime;
                CurrentMood = orderWaitElapsed >= business.AngryAfterSeconds ? Mood.Angry : orderWaitElapsed >= business.ImpatientAfterSeconds ? Mood.Impatient : Mood.Happy;
            }
            else if (Stage == CustomerStage.Exit || Stage == CustomerStage.Payment) CurrentMood = Mood.Happy;
            else CurrentMood = Mood.Happy;
            if (Stage != CustomerStage.ReadyToOrder) orderWaitElapsed = 0f;
        }
    }
}
