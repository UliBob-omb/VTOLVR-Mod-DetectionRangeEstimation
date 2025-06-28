global using static DetectionRangeEstimation.Logger;
using System.Collections.Generic;
using ModLoader.Framework;
using ModLoader.Framework.Attributes;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VTOLVR.Multiplayer;

/*
 * Bug Checks:
 * -check if quicksaves or quickloads create any bugs with vanilla
 * -any issues with players who do not own the DLC?
 *
 * Polish:
 * -ensure that return directions are correct and accurate
 * 
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

        // Player Actor
        private Dictionary<string, PilotSave> _pilotSaves = [];
        private static PilotSave _currentPilot = null;
        private bool _recalcNeeded = false;
        private Actor _playerActor = null;
        private MFDCommsPage _commsPage = null;
        private int _eqNumToJett = 0;

        // Data
        private List<Vector3> _returnDirections = [];
        private readonly List<string> _radarNames = new() { { "E. WRN (EW/AD)",
                                                              "AWACS (E4/AE)",
                                                              "MAD-4 (4)",
                                                              "NMSSLR (LR)",
                                                              "SAMSA (DS/E)",
                                                              "MBLRDR (SL)",
                                                              "CRUISER (DM)",
                                                              "NMSSVRT (MC)",
                                                              "CTRLRDR (SR)",
                                                              "CTRLRDR (R)",
                                                              "SAAW (SA)",
                                                              "EF-24 (24/EF)",
                                                              "F-45A (45/FS)",
                                                              "FA-26B (26/FA)",
                                                              "T-55 (55/T)",
                                                              "ASF-58 (F+)",
                                                              "ASF-30/33 (F)"} }; 
        private readonly Dictionary<string, float> _RCS1Range = new() { { "E. WRN (EW/AD)", 22.59f },
                                                                        { "AWACS (E4/AE)",  18.70f },
                                                                        { "MAD-4 (4)",      04.83f },
                                                                        { "NMSSLR (LR)",    04.68f },
                                                                        { "SAMSA (DS/E)",   04.09f },
                                                                        { "MBLRDR (SL)",    03.72f },
                                                                        { "CRUISER (DM)",   03.38f },
                                                                        { "NMSSVRT (MC)",   03.05f },
                                                                        { "CTRLRDR (SR)",   03.05f },
                                                                        { "CTRLRDR (R)",    02.86f },
                                                                        { "SAAW (SA)",      01.21f },
                                                                        { "EF-24 (24/EF)",  04.95f },
                                                                        { "F-45A (45/FS)",  04.68f },
                                                                        { "FA-26B (26/FA)", 04.27f },
                                                                        { "T-55 (55/T)",    03.87f },
                                                                        { "ASF-58 (F+)",    04.09f },
                                                                        { "ASF-30/33 (F)",  03.91f }};
        public Dictionary<string, List<float>> _detectionRange = new() {{ "E. WRN (EW/AD)", [0f,0f,0f,0f,0f,0f] },
                                                                        { "AWACS (E4/AE)",  [0f,0f,0f,0f,0f,0f] },
                                                                        { "MAD-4 (4)",      [0f,0f,0f,0f,0f,0f] },
                                                                        { "NMSSLR (LR)",    [0f,0f,0f,0f,0f,0f] },
                                                                        { "SAMSA (DS/E)",   [0f,0f,0f,0f,0f,0f] },
                                                                        { "MBLRDR (SL)",    [0f,0f,0f,0f,0f,0f] },
                                                                        { "CRUISER (DM)",   [0f,0f,0f,0f,0f,0f] },
                                                                        { "NMSSVRT (MC)",   [0f,0f,0f,0f,0f,0f] },
                                                                        { "CTRLRDR (SR)",   [0f,0f,0f,0f,0f,0f] },
                                                                        { "CTRLRDR (R)",    [0f,0f,0f,0f,0f,0f] },
                                                                        { "SAAW (SA)",      [0f,0f,0f,0f,0f,0f] },
                                                                        { "EF-24 (24/EF)",  [0f,0f,0f,0f,0f,0f] },
                                                                        { "F-45A (45/FS)",  [0f,0f,0f,0f,0f,0f] },
                                                                        { "FA-26B (26/FA)", [0f,0f,0f,0f,0f,0f] },
                                                                        { "T-55 (55/T)",    [0f,0f,0f,0f,0f,0f] },
                                                                        { "ASF-58 (F+)",    [0f,0f,0f,0f,0f,0f] },
                                                                        { "ASF-30/33 (F)",  [0f,0f,0f,0f,0f,0f] }};
        // MFD Page/Portal Management
        public int currentRdr = 0;
        private bool _isPortal = false;
        private bool _isFirstActorSpawn = true;
        public MFDPortalManager mfdPMngr = null;
        public MFDPHome homePortal = null;
        public MFDManager mfdMngr = null;
        public GameObject homePgInstance = null;
        public MFDPage homePage = null;
        private AssetBundle _MFDpgPrefabRef;
        public GameObject distPgInstance;
        public GameObject distPortPgInstance;

        private void Awake()
        {
            ModFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            Log($"Detection Range Estimation Awake at {ModFolder}");

            _MFDpgPrefabRef = AssetBundle.LoadFromFile($"{ModFolder}\\rdrdtctdistpage");
            if (_MFDpgPrefabRef == null)
            {
                LogError($"MFD Page/Portal asset bundle could not be loaded!");
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
            if (_isFirstActorSpawn && FlightSceneManager.isFlightReady)
            {
                _isFirstActorSpawn = false;
                InitMFDPage();
                _recalcNeeded = true;
                return; // To mitigate race condition?
            }
            Transform transformRF = _playerActor.myTransform;
            Vector3 fwdVec = transformRF.forward.normalized;
            Vector3 upVec = transformRF.up.normalized;
            Vector3 rightVec = transformRF.right.normalized;
            _returnDirections = [-fwdVec, fwdVec, -rightVec, rightVec, -upVec, upVec];
            //    Incoming from:  front,  behind,  right,    left,      above, below  all in local space.
            
            if (FlightSceneManager.isFlightReady && _recalcNeeded)
            {
                if (_playerActor.weaponManager == null)
                {
                    LogWarn($"Weaponmanager component is a null reference!");
                    return;
                }
                _recalcNeeded = false;
                CalcDtctnDist();
                UpdateUI();
            }
        }

        // Player Actor
        private void OnUnregisteredActor(Actor actor)
        {
            if (_currentPilot == null)
            {
                return;
            }

            if (_playerActor == actor)
            {
                Log($"Player actor {_playerActor.actorName} unregistered, deinitializing...");
                _recalcNeeded = false;
                _isFirstActorSpawn = true;

                _playerActor.weaponManager.OnEquipGBroke -= OnEquipGBroke;
                _playerActor.weaponManager.OnFiredMissile -= OnFiredMissile;
                _playerActor.weaponManager.OnJettisonedEq -= OnJettisonedEq;
                _playerActor.weaponManager.OnEquipJettisonChanged -= OnEquipJettisonChanged;
                if (_playerActor.weaponManager.OnWeaponChanged.HasListeners())
                    _playerActor.weaponManager.OnWeaponChanged.RemoveListener(OnWeaponChanged);
                _playerActor.weaponManager.OnEndFire -= OnEndFire;

                _commsPage.OnRequestingRearming -= OnRearmRequest;
                _commsPage = null;

                mfdPMngr = null;
                mfdMngr = null;
                homePage = null;

                _playerActor = null;
                Log($"...player actor deinitialized.");
            }
        }
        private void OnRegisteredActor(Actor actor)
        {
            if (_currentPilot == null)
            {
                return;
            }
            if (_playerActor != null)
            {
                return;
            }

            GameObject playerObj = ObtainPlayerObj();
            if (playerObj == null)
            {
                return;
            }

            _playerActor = playerObj?.GetComponent<Actor>();
            if (_playerActor != null)
            {
                Log($"{_playerActor.name} is player {_currentPilot.pilotName}, initializing...");
                //_playerActor = actor;

                _playerActor.weaponManager.OnJettisonedEq += OnJettisonedEq;
                _playerActor.weaponManager.OnEquipJettisonChanged += OnEquipJettisonChanged;
                _playerActor.weaponManager.OnFiredMissile += OnFiredMissile;
                _playerActor.weaponManager.OnEquipGBroke += OnEquipGBroke;
                _playerActor.weaponManager.OnWeaponChanged.AddListener(OnWeaponChanged);
                _playerActor.weaponManager.OnEndFire += OnEndFire;

                _commsPage = _playerActor.GetComponentInChildren<MFDCommsPage>();
                _commsPage.OnRequestingRearming += OnRearmRequest;
                Log($"...Events initialized...");

                //InitMFDPage(); // might work here
                //Log($"...MFD manager found...");

                //_recalcNeeded = true;
                Log($"...player actor initialization done.");
            }

            //if (actor.actorName.EndsWith($"({_currentPilot.pilotName})") || actor.actorName == _currentPilot.pilotName)
        }
        private GameObject ObtainPlayerObj()
        {
            if (VTOLMPUtils.IsMultiplayer())
            {
                return VTOLMPLobbyManager.localPlayerInfo.vehicleObject;
            }

            return FlightSceneManager.instance?.playerActor != null ? FlightSceneManager.instance.playerActor.gameObject : null;
        }
        // Data update events
        private void OnEndFire()
        {
            Log($"Other weapon fired, recalculation needed.");
            _recalcNeeded = true;
        }
        private void OnWeaponChanged()
        {
            Log($"First weapon changed event after spawn or rearm, recalculation needed.");
            _playerActor.weaponManager.OnWeaponChanged.RemoveListener(OnWeaponChanged);
            if (!_isFirstActorSpawn)
            {
                _recalcNeeded = true;
            }
        }
        private void OnJettisonedEq(HPEquippable equippable)
        {
            _eqNumToJett--;
            if (_eqNumToJett <= 0)
            {
                Log($"Equipment jettisoned, recalculation needed.");
                _eqNumToJett = 0;
                _recalcNeeded = true;
            }
        }
        private void OnEquipJettisonChanged(HPEquippable hpEquip, bool state)
        {
            if (state)
            {
                _eqNumToJett++;
            }
            else
            {
                _eqNumToJett--;
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

        // Scene Management
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
       
        //Misc. (but important!)
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

            
            foreach (var item in _RCS1Range)
            {
                float curDtctRng;
                int idx = 0;
                foreach (Vector3 direction in _returnDirections)
                {
                    float rcs = _playerActor.radarCrossSection.GetCrossSection(direction);
                    curDtctRng = item.Value;
                    float newDtctRng = curDtctRng * Mathf.Sqrt(rcs);
                    _detectionRange[item.Key].AddOrSet(newDtctRng, idx);
                    //Log($"Detection range for {item.Key} of {_playerActor.actorName} is {newDtctRng} nm in direction {idx}");
                    idx++;
                }
            }
            Log($"Detection distance calculations done.");
        }

        // MFD Page/Portal
        private void InitMFDPage()
        {
            mfdMngr = null;
            mfdPMngr = null;

            string vehicleName = PilotSaveManager.currentVehicle.vehicleName;
            Log($"Player's vehicle name is {vehicleName}.");

            InstantiateVehicleAssets(vehicleName);
            if (mfdMngr == null && mfdPMngr == null)
            {
                LogError($"No MFD Manager found in player actor!");
                return;
            }
            Log($"MFD manager found");

            if (mfdMngr)
            {
                //insert modified homepage, remove & destroy vanilla homepage
                mfdMngr.homepagePrefab.gameObject.transform.SetParent(null);
                mfdMngr.homepagePrefab.gameObject.SetActive(false);
                Destroy(mfdMngr.homepagePrefab.gameObject);
                homePgInstance.transform.SetParent(mfdMngr.transform);
                mfdMngr.homepagePrefab = homePgInstance;
                mfdMngr.homepagePrefab.name = "MFDHome";
                //new page
                MFDPage mfdPg = distPgInstance.GetComponent<MFDPage>();
                mfdPg.buttons[0].OnPress.AddListener(OnDecrementRdr);
                mfdPg.buttons[1].OnPress.AddListener(OnIncrementRdr);
                homePage = mfdMngr.homepagePrefab.GetComponent<MFDPage>();

                mfdMngr.pagesDic = null;
                mfdMngr.mfdPages = null;
                mfdMngr.Awake();
                // Make sure all button gameobjects are active, so that GetComponent() will resolve.
                foreach (MFD mfd in mfdMngr.mfds)
                {
                    for (int i = 0; i < mfd.buttons.Length; i++)
                    {
                        for (int j = 0; j < mfd.buttons[i].transform.childCount; j++)
                        {
                            mfd.buttons[i].transform.GetChild(j).gameObject.SetActive(true);
                        }
                    }
                    mfd.homePage = Instantiate(mfdMngr.homepagePrefab).GetComponent<MFDPage>();
                    homePage.sortedIndex = -1;
                    mfd.manager = mfdMngr;
                    mfd.ClearButtons();
                    if (!mfd.quickloaded)
                    {
                        mfd.GoHome();
                    }
                    else
                    {
                        mfd.OpenPage(mfd.quickLoadedPage);
                    }

                    mfd.displayObject.SetActive(mfd.powerOn);
                }
                homePage.gameObject.SetActive(false);
                
                _isPortal = false;
            }
            else if (mfdPMngr)
            {
                // Init page buttons
                distPortPgInstance.transform.Find("tempMask/rdeDisplay/ButtonLeft").GetComponent<VRInteractable>().OnInteract.AddListener(OnDecrementRdr);
                distPortPgInstance.transform.Find("tempMask/rdeDisplay/ButtonRight").GetComponent<VRInteractable>().OnInteract.AddListener(OnIncrementRdr);
                MFDPortalPage page = distPortPgInstance.GetComponent<MFDPortalPage>();
                
                // add new page to menu template
                mfdPMngr.pages.Add(page);
                mfdPMngr.Awake();
                mfdPMngr.homePageTemplate.gameObject.SetActive(true);
                if (vehicleName == "EF-24G") // change menu item template height on EF-24G only
                {
                    mfdPMngr.homePageTemplate.menuItemTemplate.SetActive(true);
                    mfdPMngr.homePageTemplate.menuItemTemplate.GetComponent<RectTransform>().sizeDelta = new Vector2(52f, 30f);
                }
                mfdPMngr.Start();
                //add new page to rear mfd portal manager EF-24G
                if (vehicleName == "EF-24G")
                {
                    mfdPMngr = _playerActor.transform.Find($"PassengerOnlyObjs/RearCockpit/touchScreenAreaRear").GetComponentInChildren<MFDPortalManager>();
                    mfdPMngr.pages.Add(page);
                    mfdPMngr.Awake();
                    mfdPMngr.homePageTemplate.gameObject.SetActive(true);
                    mfdPMngr.homePageTemplate.menuItemTemplate.SetActive(true);
                    mfdPMngr.homePageTemplate.menuItemTemplate.GetComponent<RectTransform>().sizeDelta = new Vector2(52f, 30f);
                    mfdPMngr.Start();
                }

                _isPortal = true;
            }
        }
        private void InstantiateVehicleAssets(string vehicleName)
        {
            switch (vehicleName)
            {
                case "F-45A":
                    distPortPgInstance = Instantiate(_MFDpgPrefabRef.LoadAsset<GameObject>("RdrDtctDistPortal"));
                    mfdPMngr = _playerActor.transform.Find($"Local/DashCanvas/Dash/touchScreenArea").GetComponentInChildren<MFDPortalManager>(true);
                    distPortPgInstance.transform.SetParent(mfdPMngr.transform.Find("poweredObj"));
                    distPortPgInstance.transform.SetAsLastSibling();
                    break;
                case "F/A-26B":
                    distPgInstance = Instantiate(_MFDpgPrefabRef.LoadAsset<GameObject>("RdrDtctDistPage"));
                    homePgInstance = Instantiate(_MFDpgPrefabRef.LoadAsset<GameObject>("MFDHomeFA26"));
                    mfdMngr = _playerActor.transform.Find($"Local/DashCanvas/Dash/MFD1/MFDMask").GetComponentInChildren<MFDManager>(true);
                    distPgInstance.transform.SetParent(mfdMngr.transform);
                    distPgInstance.transform.SetAsLastSibling();
                    break;
                case "AV-42C":
                    distPgInstance = Instantiate(_MFDpgPrefabRef.LoadAsset<GameObject>("RdrDtctDistPage"));
                    homePgInstance = Instantiate(_MFDpgPrefabRef.LoadAsset<GameObject>("MFDHomeAV42"));
                    mfdMngr = _playerActor.transform.Find($"Local/DashCanvas/Dash/MFD1/MFDMask").GetComponentInChildren<MFDManager>(true);
                    distPgInstance.transform.SetParent(mfdMngr.transform);
                    distPgInstance.transform.SetAsLastSibling();
                    break;
                case "T-55":
                    distPgInstance = Instantiate(_MFDpgPrefabRef.LoadAsset<GameObject>("RdrDtctDistPage"));
                    homePgInstance = Instantiate(_MFDpgPrefabRef.LoadAsset<GameObject>("MFDHomeT55"));
                    mfdMngr = _playerActor.transform.Find($"PassengerOnlyObjs/DashCanvasFront/Dash/MFD1").GetComponentInChildren<MFDManager>(true);
                    distPgInstance.transform.SetParent(mfdMngr.transform);
                    distPgInstance.transform.SetAsLastSibling();
                    break;
                case "AH-94":
                    distPgInstance = Instantiate(_MFDpgPrefabRef.LoadAsset<GameObject>("RdrDtctDistPage"));
                    homePgInstance = Instantiate(_MFDpgPrefabRef.LoadAsset<GameObject>("MFDHomeAH94"));
                    mfdMngr = _playerActor.transform.Find($"PassengerOnlyObjects/DashCanvas/Rear/MFD1/MFDMask").GetComponentInChildren<MFDManager>(true);
                    distPgInstance.transform.SetParent(mfdMngr.transform);
                    distPgInstance.transform.SetAsLastSibling();
                    break;
                case "EF-24G":
                    distPortPgInstance = Instantiate(_MFDpgPrefabRef.LoadAsset<GameObject>("RdrDtctDistPortal"));
                    mfdPMngr = _playerActor.transform.Find($"PassengerOnlyObjs/FrontCockpit/DashTransform/touchScreenArea").GetComponentInChildren<MFDPortalManager>(true);
                    distPortPgInstance.transform.SetParent(mfdPMngr.transform.Find("poweredObj/-- pages --"));
                    distPortPgInstance.transform.SetAsLastSibling();
                    break;
                default:
                    LogError($"Vehicle {vehicleName} is not in list of supported vehicles!");
                    break;
            }
        }
        private void OnDecrementRdr()
        {
            currentRdr--;
            CorrectIndex();
            UpdateUI();
        }
        private void OnIncrementRdr()
        {
            currentRdr++;
            CorrectIndex();
            UpdateUI();
        }
        private void CorrectIndex()
        {
            if (currentRdr < 0)
            {
                currentRdr = _detectionRange.Keys.Count - 1;
            }
            if (currentRdr >= _detectionRange.Keys.Count)
            {
                currentRdr = 0;
            }
        }
        public void UpdateUI()
        {
            string radarName = _radarNames[currentRdr];
            if (!_isPortal)
            {
                Text rdrNameText = distPgInstance.transform.Find("RadarName/Opfor Name").GetComponent<Text>();
                rdrNameText.text = radarName;
                for (int i = 0; i < 6; i++) 
                { 
                    Text distText = distPgInstance.transform.Find($"Direction {i}/DetectDistText").GetComponent<Text>();
                    distText.text = _detectionRange[radarName][i].ToString("0.00") + "nm";
                }
            }
            if (_isPortal)
            {
                Text rdrNameText = distPortPgInstance.transform.Find("tempMask/rdeDisplay/OpforRadarIndic/OPFORname").GetComponent<Text>();
                rdrNameText.text = radarName;
                for (int i = 0; i < 6; i++)
                {
                    Text distText = distPortPgInstance.transform.Find($"tempMask/rdeDisplay/Direction {i}/detectDist").GetComponent<Text>();
                    distText.text = _detectionRange[radarName][i].ToString("0.00") + "nm";
                }
            }
        }

        public override void UnLoad()
        {
            // Destroy any objects
            _MFDpgPrefabRef.Unload(true);

            SceneManager.activeSceneChanged -= OnSceneChange;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            TargetManager.instance.OnRegisteredActor -= OnRegisteredActor;
            TargetManager.instance.OnUnregisteredActor -= OnUnregisteredActor;
        }
    }
}