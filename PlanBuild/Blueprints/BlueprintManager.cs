﻿using Jotunn.Configs;
using Jotunn.Managers;
using PlanBuild.ModCompat;
using PlanBuild.Plans;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PlanBuild.Blueprints
{
    internal class BlueprintManager
    {
        private static BlueprintManager _instance;
        public static BlueprintManager Instance
        {
            get
            {
                if (_instance == null) _instance = new BlueprintManager();
                return _instance;
            }
        }

        internal static ButtonConfig planSwitchButton;

        internal static BlueprintDictionary LocalBlueprints;
        internal static BlueprintDictionary ServerBlueprints;

        private float SelectionRadius = 10.0f;
        private float PlacementOffset = 0f;
        private float CameraOffset = 5.0f;

        private float OriginalPlaceDistance;

        internal const float HighlightTimeout = 0.5f;
        private float LastHightlightTime = 0f;

        private Piece LastHoveredPiece;

        internal void Init()
        {
            Jotunn.Logger.LogInfo("Initializing BlueprintManager");

            try
            {
                // Init lists
                LocalBlueprints = new BlueprintDictionary(BlueprintLocation.Local);
                ServerBlueprints = new BlueprintDictionary(BlueprintLocation.Server);

                // Init config
                BlueprintConfig.Init();

                // Init sync
                BlueprintSync.Init();

                // Init Commands
                BlueprintCommands.Init();

                // Init GUI
                BlueprintGUI.Init();

                // Create KeyHints if and when PixelFix is created
                GUIManager.OnPixelFixCreated += CreateCustomKeyHints;

                // Create blueprint prefabs when all pieces were registered
                // Some may still fail, these will be retried every time the blueprint rune is opened
                PieceManager.OnPiecesRegistered += RegisterKnownBlueprints;

                // Hooks
                On.PieceTable.UpdateAvailable += OnUpdateAvailable;
                On.Player.PieceRayTest += OnPieceRayTest;
                On.Player.UpdateWearNTearHover += OnUpdateWearNTearHover;
                On.Player.UpdatePlacement += OnUpdatePlacement;
                On.Player.UpdatePlacementGhost += OnUpdatePlacementGhost;
                On.Player.PlacePiece += BeforePlaceBlueprintPiece;
                On.GameCamera.UpdateCamera += AdjustCameraHeight;
                On.Humanoid.EquipItem += OnEquipItem;
                On.Humanoid.UnequipItem += OnUnequipItem;
                On.ZNet.OnDestroy += ResetServerBlueprints;

            }
            catch (Exception ex)
            {
                Jotunn.Logger.LogError($"{ex.StackTrace}");
            }
        }

        /// <summary>
        ///     Determine if a piece can be captured in a blueprint
        /// </summary>
        /// <param name="piece"></param>
        /// <returns></returns>
        public bool CanCapture(Piece piece)
        {
            if (piece.name.StartsWith(BlueprintRunePrefab.BlueprintSnapPointName) || piece.name.StartsWith(BlueprintRunePrefab.BlueprintCenterPointName))
            {
                return true;
            }
            return piece.GetComponent<PlanPiece>() != null || PlanBuildPlugin.CanCreatePlan(piece);
        }

        /// <summary>
        ///     Get all pieces on a given position in a given radius
        /// </summary>
        /// <param name="position"></param>
        /// <param name="radius"></param>
        /// <returns></returns>
        public List<Piece> GetPiecesInRadius(Vector3 position, float radius)
        {
            List<Piece> result = new List<Piece>();
            foreach (var piece in Piece.m_allPieces)
            {
                if (Vector2.Distance(new Vector2(position.x, position.z), new Vector2(piece.transform.position.x, piece.transform.position.z)) <= radius
                    && CanCapture(piece))
                {
                    result.Add(piece);
                }
            }
            return result;
        }

        public void HighlightPiecesInRadius(Vector3 startPosition, float radius, Color color)
        {
            if (Time.time < LastHightlightTime + HighlightTimeout)
            {
                return;
            }
            foreach (var piece in GetPiecesInRadius(startPosition, radius))
            {
                if (piece.TryGetComponent(out WearNTear wearNTear))
                {
                    wearNTear.Highlight(color);
                }
            }
            LastHightlightTime = Time.time;
        }

        public void HighlightHoveredBlueprint()
        {
            if (Time.time > LastHightlightTime + HighlightTimeout)
            {
                if (LastHoveredPiece != null && LastHoveredPiece.TryGetComponent(out PlanPiece hoveredPlanPiece))
                {
                    ZDOID blueprintID = hoveredPlanPiece.GetBlueprintID();
                    if (blueprintID != ZDOID.None)
                    {
                        foreach (PlanPiece planPiece in GetPlanPiecesInBlueprint(blueprintID))
                        {
                            planPiece.m_wearNTear.Highlight(Color.red);
                        }
                    }
                }
                LastHightlightTime = Time.time;
            }
        }

        public List<PlanPiece> GetPlanPiecesInBlueprint(ZDOID blueprintID)
        {
            List<PlanPiece> result = new List<PlanPiece>();
            ZDO blueprintZDO = ZDOMan.instance.GetZDO(blueprintID);
            if (blueprintZDO == null)
            {
                return result;
            }
            ZDOIDSet planPieces = GetPlanPieces(blueprintZDO);
            foreach (ZDOID pieceZDOID in planPieces)
            {
                GameObject pieceObject = ZNetScene.instance.FindInstance(pieceZDOID);
                if (pieceObject && pieceObject.TryGetComponent(out PlanPiece planPiece))
                {
                    result.Add(planPiece);
                }
            }
            return result;
        }

        public ZDOIDSet GetPlanPieces(ZDO blueprintZDO)
        {
            byte[] data = blueprintZDO.GetByteArray(PlanPiece.zdoBlueprintPiece);
            if (data == null)
            {
                return null;
            }
            return ZDOIDSet.From(new ZPackage(data));
        }

        public void PlanPieceRemovedFromBlueprint(PlanPiece planPiece)
        {
            ZDOID blueprintID = planPiece.GetBlueprintID();
            if (blueprintID == ZDOID.None)
            {
                return;
            }

            ZDO blueprintZDO = ZDOMan.instance.GetZDO(blueprintID);
            if (blueprintZDO == null)
            {
                return;
            }
            ZDOIDSet planPieces = GetPlanPieces(blueprintZDO);
            planPieces?.Remove(planPiece.GetPlanPieceID());
            if (planPieces == null || planPieces.Count() == 0)
            {
                GameObject blueprintObject = ZNetScene.instance.FindInstance(blueprintID);
                if (blueprintObject)
                {
                    ZNetScene.instance.Destroy(blueprintObject);
                }
            }
            else
            {
                blueprintZDO.Set(PlanPiece.zdoBlueprintPiece, planPieces.ToZPackage().GetArray());
            }
        }

        public void RegisterKnownBlueprints()
        {
            // Client only
            if (ZNet.instance != null && !ZNet.instance.IsDedicated())
            {
                Jotunn.Logger.LogMessage("Registering known blueprints");

                // Create prefabs for all known blueprints
                foreach (var bp in LocalBlueprints.Values)
                {
                    bp.CreatePrefab();
                }
            }
        }

        private void CreateCustomKeyHints()
        {
            planSwitchButton = new ButtonConfig
            {
                Name = "RuneModeToggle",
                Config = BlueprintConfig.planSwitchConfig,
                HintToken = "$hud_bp_toggle_plan_mode"
            };
            InputManager.Instance.AddButton(PlanBuildPlugin.PluginGUID, planSwitchButton);

            GUIManager.Instance.AddKeyHint(new KeyHintConfig
            {
                Item = BlueprintRunePrefab.BlueprintRuneName,
                ButtonConfigs = new[]
                {
                    new ButtonConfig { Name = planSwitchButton.Name, HintToken = "$hud_bp_switch_to_blueprint_mode" },
                    new ButtonConfig { Name = "BuildMenu", HintToken = "$hud_buildmenu" }
                }
            });

            GUIManager.Instance.AddKeyHint(new KeyHintConfig
            {
                Item = BlueprintRunePrefab.BlueprintRuneName,
                Piece = BlueprintRunePrefab.MakeBlueprintName,
                ButtonConfigs = new[]
                {
                    new ButtonConfig { Name = planSwitchButton.Name, HintToken = "$hud_bp_switch_to_plan_mode" },
                    new ButtonConfig { Name = "Attack", HintToken = "$hud_bpcapture" },
                    new ButtonConfig { Name = "BuildMenu", HintToken = "$hud_buildmenu" },
                    new ButtonConfig { Name = "Ctrl", HintToken = "$hud_bpcapture_highlight" },
                    new ButtonConfig { Name = "Scroll", Axis = "Mouse ScrollWheel", HintToken = "$hud_bpradius" }
                }
            });

            GUIManager.Instance.AddKeyHint(new KeyHintConfig
            {
                Item = BlueprintRunePrefab.BlueprintRuneName,
                Piece = BlueprintRunePrefab.BlueprintSnapPointName,
                ButtonConfigs = new[]
                {
                    new ButtonConfig { Name = planSwitchButton.Name, HintToken = "$hud_bp_switch_to_plan_mode" },
                    new ButtonConfig { Name = "Attack", HintToken = "$hud_bpsnappoint" },
                    new ButtonConfig { Name = "BuildMenu", HintToken = "$hud_buildmenu" },
                    new ButtonConfig { Name = "Scroll", Axis = "Mouse ScrollWheel", HintToken = "$hud_bprotate" },
                }
            });

            GUIManager.Instance.AddKeyHint(new KeyHintConfig
            {
                Item = BlueprintRunePrefab.BlueprintRuneName,
                Piece = BlueprintRunePrefab.BlueprintCenterPointName,
                ButtonConfigs = new[]
                {
                    new ButtonConfig { Name = planSwitchButton.Name, HintToken = "$hud_bp_switch_to_plan_mode" },
                    new ButtonConfig { Name = "Attack", HintToken = "$hud_bpcenterpoint" },
                    new ButtonConfig { Name = "BuildMenu", HintToken = "$hud_buildmenu" },
                    new ButtonConfig { Name = "Scroll", Axis = "Mouse ScrollWheel", HintToken = "$hud_bprotate" },
                }
            });

            GUIManager.Instance.AddKeyHint(new KeyHintConfig
            {
                Item = BlueprintRunePrefab.BlueprintRuneName,
                Piece = BlueprintRunePrefab.UndoBlueprintName,
                ButtonConfigs = new[]
                {
                    new ButtonConfig { Name = planSwitchButton.Name, HintToken = "$hud_bp_switch_to_plan_mode" },
                    new ButtonConfig { Name = "Attack", HintToken = "$hud_bpundo" },
                    new ButtonConfig { Name = "BuildMenu", HintToken = "$hud_buildmenu" }
                }
            });

            GUIManager.Instance.AddKeyHint(new KeyHintConfig
            {
                Item = BlueprintRunePrefab.BlueprintRuneName,
                Piece = BlueprintRunePrefab.DeletePlansName,
                ButtonConfigs = new[]
                {
                    new ButtonConfig { Name = planSwitchButton.Name, HintToken = "$hud_bp_switch_to_plan_mode" },
                    new ButtonConfig { Name = "Attack", HintToken = "$hud_bpdelete" },
                    new ButtonConfig { Name = "BuildMenu", HintToken = "$hud_buildmenu" },
                    new ButtonConfig { Name = "Ctrl", HintToken = "$hud_bpdelete_highlight" },
                    new ButtonConfig { Name = "Scroll", Axis = "Mouse ScrollWheel", HintToken = "$hud_bpradius" }
                }
            });

            GUIManager.OnPixelFixCreated -= CreateCustomKeyHints;
        }

        private void OnUpdateAvailable(On.PieceTable.orig_UpdateAvailable orig, PieceTable self, HashSet<string> knownRecipies, Player player, bool hideUnavailable, bool noPlacementCost)
        {
            RegisterKnownBlueprints();
            orig(self, knownRecipies, player, hideUnavailable, noPlacementCost);
        }

        private bool OnPieceRayTest(On.Player.orig_PieceRayTest orig, Player self, out Vector3 point, out Vector3 normal, out Piece piece, out Heightmap heightmap, out Collider waterSurface, bool water)
        {
            bool result = orig(self, out point, out normal, out piece, out heightmap, out waterSurface, water);
            LastHoveredPiece = piece;
            if (result && PlacementOffset != 0)
            {
                point += new Vector3(0, PlacementOffset, 0);
            }
            return result;
        }

        private void OnUpdateWearNTearHover(On.Player.orig_UpdateWearNTearHover orig, Player self)
        {
            Piece piece = Player.m_localPlayer.GetSelectedPiece();
            if (piece != null)
            {
                if (piece.name.StartsWith(BlueprintRunePrefab.MakeBlueprintName)
                    || piece.name.StartsWith(BlueprintRunePrefab.DeletePlansName)
                    || piece.name.StartsWith(BlueprintRunePrefab.UndoBlueprintName))
                {
                    return;
                }
            }

            orig(self);
        }

        /// <summary>
        ///     Show and change blueprint selection radius
        /// </summary>
        private void OnUpdatePlacement(On.Player.orig_UpdatePlacement orig, Player self, bool takeInput, float dt)
        {
            orig(self, takeInput, dt);

            if (self.m_placementGhost && takeInput)
            {
                var piece = self.m_placementGhost.GetComponent<Piece>();
                if (piece != null)
                {
                    // Capture Blueprint
                    if (piece.name.StartsWith(BlueprintRunePrefab.MakeBlueprintName) && !piece.IsCreator())
                    {
                        if (!self.m_placementMarkerInstance)
                        {
                            return;
                        }

                        if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                        {
                            HighlightPiecesInRadius(self.m_placementMarkerInstance.transform.position, Instance.SelectionRadius, Color.green);
                        }

                        float scrollWheel = Input.GetAxis("Mouse ScrollWheel");
                        if (scrollWheel != 0f)
                        {
                            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                            {
                                UpdateCameraOffset(scrollWheel);
                            }
                            else
                            {
                                UpdateSelectionRadius(scrollWheel);
                            }
                        }

                        var circleProjector = self.m_placementMarkerInstance.GetComponent<CircleProjector>();
                        if (circleProjector == null)
                        {
                            circleProjector = self.m_placementMarkerInstance.AddComponent<CircleProjector>();
                            circleProjector.m_prefab = PrefabManager.Instance.GetPrefab("piece_workbench").GetComponentInChildren<CircleProjector>().m_prefab;

                            // Force calculation of segment count
                            circleProjector.m_radius = -1;
                            circleProjector.Start();
                        }

                        if (circleProjector.m_radius != Instance.SelectionRadius)
                        {
                            circleProjector.m_radius = Instance.SelectionRadius;
                            circleProjector.m_nrOfSegments = (int)circleProjector.m_radius * 4;
                            circleProjector.Update();
                            Jotunn.Logger.LogDebug($"Setting radius to {Instance.SelectionRadius}");
                        }
                    }
                    // Place Blueprint
                    else if (piece.name.StartsWith(Blueprint.PieceBlueprintName))
                    {
                        // Destroy placement marker instance to get rid of the circleprojector
                        if (self.m_placementMarkerInstance)
                        {
                            Object.DestroyImmediate(self.m_placementMarkerInstance);
                        }

                        // Reset rotation when changing camera
                        float scrollWheel = Input.GetAxis("Mouse ScrollWheel");
                        if (scrollWheel != 0f)
                        {
                            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                            {
                                UpdateCameraOffset(scrollWheel);
                                UndoRotation(self, scrollWheel);
                            }
                            else if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                            {
                                UpdatePlacementOffset(scrollWheel);
                                UndoRotation(self, scrollWheel);
                            }
                        }
                    }
                    // Delete Plan
                    else if (piece.name.StartsWith(BlueprintRunePrefab.DeletePlansName))
                    {
                        if (!self.m_placementMarkerInstance)
                        {
                            return;
                        }

                        if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                        {
                            HighlightPiecesInRadius(self.m_placementMarkerInstance.transform.position, Instance.SelectionRadius, Color.red);
                        }

                        float scrollWheel = Input.GetAxis("Mouse ScrollWheel");
                        if (scrollWheel != 0)
                        {
                            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                            {
                                UpdateCameraOffset(scrollWheel);
                                UndoRotation(self, scrollWheel);
                            }
                            else
                            {
                                UpdateSelectionRadius(scrollWheel);
                            }
                        }

                        var circleProjector = self.m_placementMarkerInstance.GetComponent<CircleProjector>();
                        if (circleProjector == null)
                        {
                            circleProjector = self.m_placementMarkerInstance.AddComponent<CircleProjector>();
                            circleProjector.m_prefab = PrefabManager.Instance.GetPrefab("piece_workbench").GetComponentInChildren<CircleProjector>().m_prefab;

                            // Force calculation of segment count
                            circleProjector.m_radius = -1;
                            circleProjector.Start();
                        }

                        if (circleProjector.m_radius != Instance.SelectionRadius)
                        {
                            circleProjector.m_radius = Instance.SelectionRadius;
                            circleProjector.m_nrOfSegments = (int)circleProjector.m_radius * 4;
                            circleProjector.Update();
                            Jotunn.Logger.LogDebug($"Setting radius to {Instance.SelectionRadius}");
                        }
                    }
                    // Undo Blueprint
                    else if (piece.name.StartsWith(BlueprintRunePrefab.UndoBlueprintName))
                    {
                        // Destroy placement marker instance to get rid of the circleprojector
                        if (self.m_placementMarkerInstance)
                        {
                            Object.DestroyImmediate(self.m_placementMarkerInstance);
                        }

                        HighlightHoveredBlueprint();
                    }
                    else
                    {
                        // Destroy placement marker instance to get rid of the circleprojector
                        if (self.m_placementMarkerInstance)
                        {
                            Object.DestroyImmediate(self.m_placementMarkerInstance);
                        }

                        // Reset camera
                        Instance.CameraOffset = 5f;
                        Instance.PlacementOffset = 0f;
                    }
                }
            }
        }

        /// <summary>
        ///     Flatten the circle selector transform
        /// </summary>
        /// <param name="orig"></param>
        /// <param name="self"></param>
        /// <param name="flashGuardStone"></param>
        private void OnUpdatePlacementGhost(On.Player.orig_UpdatePlacementGhost orig, Player self, bool flashGuardStone)
        {
            orig(self, flashGuardStone);

            if (self.m_placementMarkerInstance && self.m_placementGhost && 
                (self.m_placementGhost.name == BlueprintRunePrefab.MakeBlueprintName
                || self.m_placementGhost.name == BlueprintRunePrefab.DeletePlansName)
               )
            {
                self.m_placementMarkerInstance.transform.up = Vector3.back;
            }
        }

        private void UpdatePlacementOffset(float scrollWheel)
        {
            bool scrollingDown = scrollWheel < 0f;
            if (BlueprintConfig.invertPlacementOffsetScrollConfig.Value)
            {
                scrollingDown = !scrollingDown;
            }
            if (scrollingDown)
            {
                Instance.PlacementOffset -= BlueprintConfig.placementOffsetIncrementConfig.Value;
            }
            else
            {
                Instance.PlacementOffset += BlueprintConfig.placementOffsetIncrementConfig.Value;
            }
        }

        private void UndoRotation(Player player, float scrollWheel)
        {
            if (scrollWheel < 0f)
            {
                player.m_placeRotation++;
            }
            else
            {
                player.m_placeRotation--;
            }
        }

        private void UpdateCameraOffset(float scrollWheel)
        {
            // TODO: base min/max off of selected piece dimensions
            float minOffset = 2f;
            float maxOffset = 20f;
            bool scrollingDown = scrollWheel < 0f;
            if (BlueprintConfig.invertCameraOffsetScrollConfig.Value)
            {
                scrollingDown = !scrollingDown;
            }
            if (scrollingDown)
            {
                Instance.CameraOffset = Mathf.Clamp(Instance.CameraOffset += BlueprintConfig.cameraOffsetIncrementConfig.Value, minOffset, maxOffset);
            }
            else
            {
                Instance.CameraOffset = Mathf.Clamp(Instance.CameraOffset -= BlueprintConfig.cameraOffsetIncrementConfig.Value, minOffset, maxOffset);
            }
        }

        private void UpdateSelectionRadius(float scrollWheel)
        {
            bool scrollingDown = scrollWheel < 0f;
            if (BlueprintConfig.invertSelectionScrollConfig.Value)
            {
                scrollingDown = !scrollingDown;
            }
            if (scrollingDown)
            {
                Instance.SelectionRadius -= BlueprintConfig.selectionIncrementConfig.Value;
                if (Instance.SelectionRadius < 2f)
                {
                    Instance.SelectionRadius = 2f;
                }
            }
            else
            {
                Instance.SelectionRadius += BlueprintConfig.selectionIncrementConfig.Value;
            }
        }

        /// <summary>
        ///     Add some camera height while planting a blueprint
        /// </summary>
        private void AdjustCameraHeight(On.GameCamera.orig_UpdateCamera orig, GameCamera self, float dt)
        {
            orig(self, dt);

            if (PatcherBuildCamera.UpdateCamera
                && Player.m_localPlayer
                && Player.m_localPlayer.InPlaceMode()
                && Player.m_localPlayer.m_placementGhost)
            {
                var pieceName = Player.m_localPlayer.m_placementGhost.name;
                if (pieceName.StartsWith("make_blueprint")
                    || pieceName.StartsWith("piece_blueprint")
                    || pieceName.StartsWith("delete_plans"))
                {
                    self.transform.position += new Vector3(0, Instance.CameraOffset, 0);
                }
            }
        }

        /// <summary>
        ///     Incept placing of the meta pieces.
        ///     Cancels the real placement of the placeholder pieces.
        /// </summary>
        private bool BeforePlaceBlueprintPiece(On.Player.orig_PlacePiece orig, Player self, Piece piece)
        {
            // Client only
            if (!ZNet.instance.IsDedicated())
            {
                // Capture a new blueprint
                if (piece.name == "make_blueprint")
                {
                    return MakeBlueprint(self);
                }
                // Place a known blueprint
                if (Player.m_localPlayer.m_placementStatus == Player.PlacementStatus.Valid
                    && piece.name != BlueprintRunePrefab.BlueprintSnapPointName
                    && piece.name != BlueprintRunePrefab.BlueprintCenterPointName
                    && piece.name.StartsWith("piece_blueprint"))
                {
                    return PlaceBlueprint(self, piece);
                }
                else if (piece.name.StartsWith(BlueprintRunePrefab.UndoBlueprintName))
                {
                    return UndoBlueprint();
                }
                else if (piece.name.StartsWith(BlueprintRunePrefab.DeletePlansName))
                {
                    return DeletePlans(self);
                }
            }

            return orig(self, piece);
        }

        private bool MakeBlueprint(Player self)
        {
            var circleProjector = self.m_placementGhost.GetComponent<CircleProjector>();
            if (circleProjector != null)
            {
                Object.Destroy(circleProjector);
            }

            var bpname = $"blueprint{LocalBlueprints.Count() + 1:000}";
            Jotunn.Logger.LogInfo($"Capturing blueprint {bpname}");

            var bp = new Blueprint();
            Vector3 capturePosition = self.m_placementMarkerInstance.transform.position;
            if (bp.Capture(capturePosition, Instance.SelectionRadius))
            {
                TextInput.instance.m_queuedSign = new Blueprint.BlueprintSaveGUI(bp);
                TextInput.instance.Show($"Save Blueprint ({bp.GetPieceCount()} pieces captured)", bpname, 50);
            }
            else
            {
                Jotunn.Logger.LogWarning($"Could not capture blueprint {bpname}");
            }

            // Don't place the piece and clutter the world with it
            return false;
        }

        private bool PlaceBlueprint(Player player, Piece piece)
        {
            string id = piece.gameObject.name.Substring(Blueprint.PieceBlueprintName.Length + 1);
            Blueprint bp = LocalBlueprints[id];
            var transform = player.m_placementGhost.transform;
            var position = transform.position;
            var rotation = transform.rotation;

            bool placeDirect = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (placeDirect && !BlueprintConfig.allowDirectBuildConfig.Value)
            {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, "$msg_direct_build_disabled");
                return false;
            }

            if (ZInput.GetButton("AltPlace"))
            {
                Vector2 extent = bp.GetExtent();
                FlattenTerrain.FlattenForBlueprint(transform, extent.x, extent.y, bp.PieceEntries);
            }

            uint cntEffects = 0u;
            uint maxEffects = 10u;

            GameObject blueprintPrefab = PrefabManager.Instance.GetPrefab(Blueprint.PieceBlueprintName);
            GameObject blueprintObject = Object.Instantiate(blueprintPrefab, position, rotation);
            ZDO blueprintZDO = blueprintObject.GetComponent<ZNetView>().GetZDO();
            blueprintZDO.Set(Blueprint.ZDOBlueprintName, bp.Name);
            ZDOIDSet createdPlans = new ZDOIDSet();

            for (int i = 0; i < bp.PieceEntries.Length; i++)
            {
                PieceEntry entry = bp.PieceEntries[i];
                // Final position
                Vector3 entryPosition = transform.TransformPoint(entry.GetPosition());

                // Final rotation
                Quaternion entryQuat = transform.rotation * entry.GetRotation();
                // Get the prefab of the piece or the plan piece
                string prefabName = entry.name;
                if (!placeDirect)
                {
                    prefabName += PlanPiecePrefab.PlannedSuffix;
                }

                GameObject prefab = PrefabManager.Instance.GetPrefab(prefabName);
                if (!prefab)
                {
                    Jotunn.Logger.LogWarning(entry.name + " not found, you are probably missing a dependency for blueprint " + bp.Name + ", not placing @ " + entryPosition);
                    continue;
                }

                // Instantiate a new object with the new prefab
                GameObject gameObject = Object.Instantiate(prefab, entryPosition, entryQuat);

                ZNetView zNetView = gameObject.GetComponent<ZNetView>();
                if (!zNetView)
                {
                    Jotunn.Logger.LogWarning("No ZNetView for " + gameObject + "!!??");
                }
                else if (gameObject.TryGetComponent(out PlanPiece planPiece))
                {
                    planPiece.PartOfBlueprint(blueprintZDO.m_uid, entry);
                    createdPlans.Add(planPiece.GetPlanPieceID());
                }

                // Register special effects
                CraftingStation craftingStation = gameObject.GetComponentInChildren<CraftingStation>();
                if (craftingStation)
                {
                    player.AddKnownStation(craftingStation);
                }
                Piece newpiece = gameObject.GetComponent<Piece>();
                if (newpiece)
                {
                    newpiece.SetCreator(player.GetPlayerID());
                }
                PrivateArea privateArea = gameObject.GetComponent<PrivateArea>();
                if (privateArea)
                {
                    privateArea.Setup(Game.instance.GetPlayerProfile().GetName());
                }
                WearNTear wearntear = gameObject.GetComponent<WearNTear>();
                if (wearntear)
                {
                    wearntear.OnPlaced();
                }
                TextReceiver textReceiver = gameObject.GetComponent<TextReceiver>();
                if (textReceiver != null)
                {
                    textReceiver.SetText(entry.additionalInfo);
                }

                // Limited build effects
                if (cntEffects < maxEffects)
                {
                    newpiece.m_placeEffect.Create(gameObject.transform.position, rotation, gameObject.transform, 1f);
                    player.AddNoise(50f);
                    cntEffects++;
                }

                // Count up player builds
                Game.instance.GetPlayerProfile().m_playerStats.m_builds++;
            }

            blueprintZDO.Set(PlanPiece.zdoBlueprintPiece, createdPlans.ToZPackage().GetArray());

            // Dont set the blueprint piece and clutter the world with it
            return false;
        }

        private bool UndoBlueprint()
        {
            if (LastHoveredPiece)
            {
                if (LastHoveredPiece.TryGetComponent(out PlanPiece planPiece))
                {
                    ZDOID blueprintID = planPiece.GetBlueprintID();
                    if (blueprintID != ZDOID.None)
                    {
                        int removedPieces = RemoveBlueprint(blueprintID);

                        Player.m_localPlayer.Message(MessageHud.MessageType.Center, Localization.instance.Localize("$msg_removed_plans", removedPieces.ToString()));
                    }
                }
            }

            return false;
        }

        private int RemoveBlueprint(ZDOID blueprintID)
        {
            int removedPieces = 0;
            Jotunn.Logger.LogInfo("Removing all pieces of blueprint " + blueprintID);
            foreach (PlanPiece planPiece in GetPlanPiecesInBlueprint(blueprintID))
            {
                planPiece.Remove();
                removedPieces++;
            }

            GameObject blueprintObject = ZNetScene.instance.FindInstance(blueprintID);
            if (blueprintObject)
            {
                ZNetScene.instance.Destroy(blueprintObject);
            }
            return removedPieces;
        }

        private bool DeletePlans(Player player)
        {
            var circleProjector = player.m_placementGhost.GetComponent<CircleProjector>();
            if (circleProjector != null)
            {
                Object.Destroy(circleProjector);
            }

            Vector3 deletePosition = player.m_placementMarkerInstance.transform.position;
            int removedPieces = 0;
            foreach (Piece pieceToRemove in GetPiecesInRadius(deletePosition, SelectionRadius))
            {
                if (pieceToRemove.TryGetComponent(out PlanPiece planPiece))
                {
                    planPiece.m_wearNTear.Remove();
                    removedPieces++;
                }
            }
            player.Message(MessageHud.MessageType.Center, Localization.instance.Localize("$msg_removed_plans", removedPieces.ToString()));

            return false;
        }

        private void OnUnequipItem(On.Humanoid.orig_UnequipItem orig, Humanoid self, ItemDrop.ItemData item, bool triggerEquipEffects)
        {
            orig(self, item, triggerEquipEffects);
            if (Player.m_localPlayer &&
                item != null && item.m_shared.m_name == BlueprintRunePrefab.BlueprintRuneItemName)
            {
                Player.m_localPlayer.m_maxPlaceDistance = OriginalPlaceDistance;
                Jotunn.Logger.LogDebug("Setting placeDistance to " + Player.m_localPlayer.m_maxPlaceDistance);
            }
        }

        private bool OnEquipItem(On.Humanoid.orig_EquipItem orig, Humanoid self, ItemDrop.ItemData item, bool triggerEquipEffects)
        {
            bool result = orig(self, item, triggerEquipEffects);
            if (Player.m_localPlayer && result &&
                item != null && item.m_shared.m_name == BlueprintRunePrefab.BlueprintRuneItemName)
            {
                OriginalPlaceDistance = Math.Max(Player.m_localPlayer.m_maxPlaceDistance, 8f);
                Player.m_localPlayer.m_maxPlaceDistance = BlueprintConfig.rayDistanceConfig.Value;
                Jotunn.Logger.LogDebug("Setting placeDistance to " + Player.m_localPlayer.m_maxPlaceDistance);
            }
            return result;
        }

        private void ResetServerBlueprints(On.ZNet.orig_OnDestroy orig, ZNet self)
        {
            ServerBlueprints?.Clear();
            orig(self);
        }
    }
}