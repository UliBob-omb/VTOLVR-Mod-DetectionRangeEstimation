global using static DetectionRangeEstimation.Logger;
using System.Collections.Generic;
using ModLoader.Framework;
using ModLoader.Framework.Attributes;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using HarmonyLib;
using System.Runtime.CompilerServices;
using System.Diagnostics.Eventing.Reader;
using System;
using UnityEngine.Events;
using UnityEngine.UI;
using System.Collections.ObjectModel;

/*
 * TODO:
 * 
 * -verify detection range correctness
 * -check if player death creates any bugs
 * -check to see if new readonly on _RCS1Range causes any issues
 * -display these stats in nm *somewhere* in-game (kneeboard? New MFD screen?)
 * --study MFDs in all planes to determine best approach
 * --display stats in new mfd page, initializing with new gameobject.
 * --be able to config opfor radar
 * --make event for config button press
 */

/* RCS process:
 * 1. Check for transform ref; create one if not found
 * 2. If spherical RCS is overriding and over 0, return the value.
 * 3. Take input direction (world space) and translte it into local space of the vehicle, then normalize it and reverse it.
 * 4. initialize some nums:
 * 4a. num1 at 0
 * 4b. num2 at 0
 * 4c. number of radar returns
 * 4d. num4 at 0, integer
 * 5. While num4 is less than num of radar returns:
 * 5a. calculate the dot product of the direction to the return normal
 * 5b. if the dot product is greater than 0, calculate the dot product result to the 15th power, 
 *     multiply it to the returnvalue of the current return and accumulate it to num1.
 * 5c. Accumulate the powered dot product to num2
 * 5d. increment num4
 * 6. Make final RCS calculation: 100 * overridemultiplier * RCS size * adjusted accumulated return values
 *    all divided by accumulated return values
 * 7. Add RCS of equipped pylons and weapons to final RCS calculation
 * 8. Return Final RCS Value for the given incoming direction (float)
 */

/* Radar detection range calc process:
 * - equals sqrt(adjusted jammer gain * rcs * own transmission strength * own receiver sensitivity) if not jammed
 * - Alternatively, use the precalculated RCS 1 to multiply to sqrt(rcs) of the vehicle <-- This is what we'll use
 */

namespace DetectionRangeEstimation
{
    [ItemId("ulibomber.DetectionRangeEstimation")] // Harmony ID for your mod, make sure this is unique
    public class Main : VtolMod
    {
        public string ModFolder;

        private Dictionary<string, PilotSave> _pilotSaves = [];
        private static PilotSave _currentPilot = null;

        private bool _recalcNeeded = false;
        private Actor _playerActor = null;
        private MFDCommsPage _commsPage = null;
        private int _eqNumToJett = 0;

        private List<Vector3> _returnDirections = [];
        private readonly Dictionary<string, float> _RCS1Range = new() { { "FA-26B",    04.27f },
                                                                        { "AV-42",     00.00f },
                                                                        { "F-45A",     04.68f },
                                                                        { "AH-94",     00.00f },
                                                                        { "T-55",      03.87f },
                                                                        { "EF-24",     04.95f },
                                                                        { "AWACS",     18.70f },
                                                                        { "EW Rdr",    22.59f },
                                                                        { "MAD-4",     04.83f },
                                                                        { "NMSSLR",    04.68f },
                                                                        { "ASF-58",    04.09f },
                                                                        { "SAM SA",    04.09f },
                                                                        { "ASF-30/33", 03.91f },
                                                                        { "Mbl Rdr",   03.72f },
                                                                        { "Cruiser",   03.38f },
                                                                        { "NMSSVrt",   03.05f },
                                                                        { "RFrCtrl",   03.05f },
                                                                        { "BFrCtrl",   02.86f }};
        public Dictionary<string, List<float>> _detectionRange = new() {{ "FA-26B",    [0f,0f,0f,0f,0f,0f] },
                                                                        { "AV-42",     [0f,0f,0f,0f,0f,0f] },
                                                                        { "F-45A",     [0f,0f,0f,0f,0f,0f] },
                                                                        { "AH-94",     [0f,0f,0f,0f,0f,0f] },
                                                                        { "T-55",      [0f,0f,0f,0f,0f,0f] },
                                                                        { "EF-24",     [0f,0f,0f,0f,0f,0f] },
                                                                        { "AWACS",     [0f,0f,0f,0f,0f,0f] },
                                                                        { "EW Rdr",    [0f,0f,0f,0f,0f,0f] },
                                                                        { "MAD-4",     [0f,0f,0f,0f,0f,0f] },
                                                                        { "NMSSLR",    [0f,0f,0f,0f,0f,0f] },
                                                                        { "ASF-58",    [0f,0f,0f,0f,0f,0f] },
                                                                        { "SAM SA",    [0f,0f,0f,0f,0f,0f] },
                                                                        { "ASF-30/33", [0f,0f,0f,0f,0f,0f] },
                                                                        { "Mbl Rdr",   [0f,0f,0f,0f,0f,0f] },
                                                                        { "Cruiser",   [0f,0f,0f,0f,0f,0f] },
                                                                        { "NMSSVrt",   [0f,0f,0f,0f,0f,0f] },
                                                                        { "RFrCtrl",   [0f,0f,0f,0f,0f,0f] },
                                                                        { "BFrCtrl",   [0f,0f,0f,0f,0f,0f] }};
        public int currentRdr = 0;
        private bool _isPortal = false;

        public MFDPortalManager mfdPMngr = null;
        public MFDManager mfdMngr = null;
        private AssetBundle MFDpgPrefabRef;
        private AssetBundle MFDPpgPrefabRef;
        public GameObject distPgInstance;
        public GameObject distPortPgInstance;

        private void Awake()
        {
            ModFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            Log($"Detection Range Estimation Awake at {ModFolder}");

            MFDpgPrefabRef = AssetBundle.LoadFromFile($"{ModFolder}\\rdrdtctdistpage");
            MFDPpgPrefabRef = AssetBundle.LoadFromFile($"{ModFolder}\\rdrdtctdistportal");
            if (MFDpgPrefabRef == null)
            {
                LogError($"MFD Page Prefab could not be loaded!");
            }
            if (MFDPpgPrefabRef == null)
            {
                LogError($"MFD Portal Page Prefab could not be loaded!");
            }

            SceneManager.activeSceneChanged += OnSceneChange;
            SceneManager.sceneLoaded += OnSceneLoaded;
            TargetManager.instance.OnRegisteredActor += OnRegisteredActor;
            TargetManager.instance.OnUnregisteredActor += OnUnregisteredActor;

            
        }

        private void Update()
        {
            if (_playerActor == null)
            {
                return;
            }
            Transform transformRF = _playerActor.myTransform;
            Vector3 fwdVec = transformRF.forward.normalized;
            Vector3 upVec = transformRF.up.normalized;
            Vector3 rightVec = transformRF.right.normalized;
            _returnDirections = [-fwdVec, fwdVec, -rightVec, rightVec, -upVec, upVec];
            //    Incoming from:  front,  behind,  right,    left,      above, below  all in local space.
            
            if ((FlightSceneManager.isFlightReady && _recalcNeeded))
            {
                if (_playerActor.weaponManager == null)
                {
                    LogWarn($"Weaponmanager component is a null reference!");
                    return;
                }
                _recalcNeeded = false;
                CalcDtctnDist();
                // update page text with event
            }
        }

        private void OnUnregisteredActor(Actor actor)
        {
            //Log($"Actor unregistered: {actor.name}");
            if (_currentPilot == null)
            {
                return;
            }

            if (actor.actorName.EndsWith($"({_currentPilot.pilotName})"))
            {
                Log($"Player actor {_playerActor.actorName} unregistered, deinitializing...");
                _recalcNeeded = false;

                _playerActor.weaponManager.OnEquipGBroke -= OnEquipGBroke;
                _playerActor.weaponManager.OnFiredMissile -= OnFiredMissile;
                _playerActor.weaponManager.OnJettisonedEq -= OnJettisonedEq;
                _playerActor.weaponManager.OnEquipJettisonChanged -= OnEquipJettisonChanged;
                if(_playerActor.weaponManager.OnWeaponChanged.HasListeners())
                    _playerActor.weaponManager.OnWeaponChanged.RemoveListener(OnWeaponChanged);
                _playerActor.weaponManager.OnEndFire -= OnEndFire;

                _commsPage.OnRequestingRearming -= OnRearmRequest;
                _commsPage = null;

                mfdPMngr = null;
                mfdMngr = null;

                _playerActor = null;
                Log($"...player actor deinitialized.");
            }
        }
        private void OnRegisteredActor(Actor actor)
        {
            //Log($"Actor registered: {actor.name} with root actor {actor.rootActor.name}");
            if (_currentPilot == null)
            {
                return;
            }

            if (actor.actorName.EndsWith($"({_currentPilot.pilotName})"))
            {
                Log($"{actor.actorName} is player {_currentPilot.pilotName}, initializing...");
                _playerActor = actor;

                _playerActor.weaponManager.OnJettisonedEq += OnJettisonedEq;
                _playerActor.weaponManager.OnEquipJettisonChanged += OnEquipJettisonChanged;
                _playerActor.weaponManager.OnFiredMissile += OnFiredMissile;
                _playerActor.weaponManager.OnEquipGBroke += OnEquipGBroke;
                _playerActor.weaponManager.OnWeaponChanged.AddListener(OnWeaponChanged);
                _playerActor.weaponManager.OnEndFire += OnEndFire;

                _commsPage = _playerActor.GetComponentInChildren<MFDCommsPage>();
                _commsPage.OnRequestingRearming += OnRearmRequest;
                Log($"...Events initialized...");

                mfdPMngr = _playerActor.GetComponentInChildren<MFDPortalManager>();
                mfdMngr = _playerActor.GetComponentInChildren<MFDManager>();
                InitMFDPage(); // might work here
                Log($"...MFD manager found...");

                _recalcNeeded = true;
                Log($"...player actor initialization done.");
                Log($"{_playerActor.actorName} has been registered, recalculation needed.");
            }
        }

        private void OnEndFire()
        {
            Log($"Other weapon fired, recalculation needed.");
            _recalcNeeded = true;
        }
        private void OnWeaponChanged()
        {
            Log($"First weapon changed event after spawn or rearm, recalculation needed.");
            _playerActor.weaponManager.OnWeaponChanged.RemoveListener(OnWeaponChanged);

            //InitMFDPage(); // may need to move to actor registration section

            _recalcNeeded = true;
        }
        private void OnJettisonedEq(HPEquippable equippable)
        {
            _eqNumToJett--;
            if (_eqNumToJett == 0)
            {
                Log($"Equipment jettisoned, recalculation needed.");
                _recalcNeeded = true;
            }
        }
        private void OnEquipJettisonChanged(HPEquippable hpEquip, bool state)
        {
            if (state)
            {
                _eqNumToJett++;
            }
        }
        private void OnRearmRequest()
        {
            Log($"Player requested rearm.");
            _commsPage.currentRP.OnEndRearm += OnEndRearm;
        }
        private void OnEndRearm()
        {
            Log($"Rearm Ended.");
            _commsPage.currentRP.OnEndRearm -= OnEndRearm;
            _playerActor.weaponManager.OnWeaponChanged.AddListener(OnWeaponChanged);
        }
        private void OnEquipGBroke(int obj)
        {
            Log($"Equipment broken by high-G. Recalculation needed.");
            _recalcNeeded = true;
        }
        private void OnFiredMissile(Missile obj)
        {
            Log($"Missile fired. Recalculation needed.");
            _recalcNeeded = true;
        }

        private void OnSceneLoaded(Scene loadedScene, LoadSceneMode loadMode)
        {
            InitPilots();
            TargetManager.instance.OnRegisteredActor += OnRegisteredActor;
            TargetManager.instance.OnUnregisteredActor += OnUnregisteredActor;
        }
        private void OnSceneChange(Scene current, Scene next)
        {
            TargetManager.instance.OnRegisteredActor -= OnRegisteredActor;
            TargetManager.instance.OnUnregisteredActor -= OnUnregisteredActor;
            _recalcNeeded = false;
        }
       
        public void InitPilots()
        {
            _pilotSaves = PilotSaveManager.pilots;
            if (_pilotSaves.Count <= 0)
            {
                LogWarn($"No pilot saves detected!");
                return;
            }
            _currentPilot = PilotSaveManager.current;
            if (_currentPilot == null)
            {
                LogWarn($"No current pilot selected!");
                return;
            }
            Log($"Current pilot set to {_currentPilot.pilotName}");
        }
        public void CalcDtctnDist()
        {
            if (_playerActor == null)
            {
                LogWarn($"Player actor not found!");
                return;
            }
            if (_playerActor.radarCrossSection == null)
            {
                LogWarn($"Player actor {_playerActor.actorName} has no radarCrossSection component!");
                return;
            }

            int idx = 0;
            foreach (var item in _RCS1Range)
            {
                float curDtctRng = 0;
                foreach (Vector3 direction in _returnDirections)
                {
                    float rcs = _playerActor.radarCrossSection.GetCrossSection(direction);
                    curDtctRng = item.Value;
                    float newDtctRng = curDtctRng * Mathf.Sqrt(rcs);
                    _detectionRange[item.Key].AddOrSet(newDtctRng, idx);
                    Log($"Detection range for {item.Key} of {_playerActor.actorName} is {newDtctRng} nm in direction {idx}");
                }
                idx++;
            }
            Log($"Detection distance calculations done.");
        }
        private void InitMFDPage()
        {
            if (mfdMngr == null & mfdPMngr == null)
            {
                LogError($"No MFD Manager found in player actor!");
                return;
            }

            string actorName = _playerActor.actorName;
            string pilotName = $" ({_currentPilot.pilotName})";
            string vehicleName = actorName.Substring(0, actorName.Length - pilotName.Length);
            Log($"Player's vehicle name is {vehicleName}.");

            switch (vehicleName)
            {
                case "SEVTF":
                    //F-45
                    distPortPgInstance = MFDPpgPrefabRef.LoadAsset<GameObject>("RdrDtctDistPortal");
                    distPortPgInstance.transform.parent = mfdPMngr.transform.Find("poweredObj");
                    distPortPgInstance.transform.SetAsLastSibling();
                    break;
                case "FA-26B":
                    //F/A-26B
                    distPgInstance = MFDpgPrefabRef.LoadAsset<GameObject>("RdrDtctDistPage");
                    distPgInstance.transform.parent = mfdMngr.transform;
                    distPgInstance.transform.SetAsLastSibling();
                    break;
                case "VTOL4":
                    //AV-42C
                    distPgInstance = MFDpgPrefabRef.LoadAsset<GameObject>("RdrDtctDistPage");
                    distPgInstance.transform.parent = mfdMngr.transform;
                    distPgInstance.transform.SetAsLastSibling();
                    break;
                case "T-55":
                    //T-55
                    distPgInstance = MFDpgPrefabRef.LoadAsset<GameObject>("RdrDtctDistPage");
                    distPgInstance.transform.parent = mfdMngr.transform;
                    distPgInstance.transform.SetAsLastSibling();
                    break;
                case "AH-94":
                    //AH-94
                    distPgInstance = MFDpgPrefabRef.LoadAsset<GameObject>("RdrDtctDistPage");
                    distPgInstance.transform.parent = mfdMngr.transform;
                    distPgInstance.transform.SetAsLastSibling();
                    break;
                case "EF-24":
                    //EF-24G
                    distPortPgInstance = MFDPpgPrefabRef.LoadAsset<GameObject>("RdrDtctDistPortal");
                    distPortPgInstance.transform.parent = mfdPMngr.transform.Find("poweredObj").Find("-- pages --");
                    distPortPgInstance.transform.SetAsLastSibling();
                    break;
                default:
                    LogError($"Vehicle {_playerActor.name} is not in list of supported vehicles!");
                    break;
            }

            if (mfdMngr)
            {
                mfdMngr.pagesDic = null;
                mfdMngr.EnsureReady();
            }
            else if (mfdPMngr)
            {
                mfdPMngr.pages.Add(distPortPgInstance.GetComponent<MFDPortalPage>());
                mfdPMngr.Start(); // Might not work since it's meant to be a private method
            }
        }

        public override void UnLoad()
        {
            // Destroy any objects
            MFDpgPrefabRef.Unload(true);
            MFDPpgPrefabRef.Unload(true);

            SceneManager.activeSceneChanged -= OnSceneChange;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            TargetManager.instance.OnRegisteredActor -= OnRegisteredActor;
            TargetManager.instance.OnUnregisteredActor -= OnUnregisteredActor;
        }
    }

    public class TextUpdater : MonoBehaviour
    {
        
    }
}