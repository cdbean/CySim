/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Timers;
using OpenMetaverse;
using log4net;
using OpenSim.Framework;
using OpenSim.Framework.Client;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes.Animation;
using OpenSim.Region.Framework.Scenes.Types;
using OpenSim.Region.Physics.Manager;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.Framework.Scenes
{
    enum ScriptControlled : uint
    {
        CONTROL_ZERO = 0,
        CONTROL_FWD = 1,
        CONTROL_BACK = 2,
        CONTROL_LEFT = 4,
        CONTROL_RIGHT = 8,
        CONTROL_UP = 16,
        CONTROL_DOWN = 32,
        CONTROL_ROT_LEFT = 256,
        CONTROL_ROT_RIGHT = 512,
        CONTROL_LBUTTON = 268435456,
        CONTROL_ML_LBUTTON = 1073741824
    }

    struct ScriptControllers
    {
        public UUID itemID;
        public uint objID;
        public ScriptControlled ignoreControls;
        public ScriptControlled eventControls;
    }

    public delegate void SendCourseLocationsMethod(UUID scene, ScenePresence presence, List<Vector3> coarseLocations, List<UUID> avatarUUIDs);

    public class ScenePresence : EntityBase, ISceneEntity
    {
//        ~ScenePresence()
//        {
//            m_log.Debug("[ScenePresence] Destructor called");
//        }

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly byte[] BAKE_INDICES = new byte[] { 8, 9, 10, 11, 19, 20 };
//        private static readonly byte[] DEFAULT_TEXTURE = AvatarAppearance.GetDefaultTexture().GetBytes();
        private static readonly Array DIR_CONTROL_FLAGS = Enum.GetValues(typeof(Dir_ControlFlags));
        private static readonly Vector3 HEAD_ADJUSTMENT = new Vector3(0f, 0f, 0.3f);
        
        /// <summary>
        /// Experimentally determined "fudge factor" to make sit-target positions
        /// the same as in SecondLife. Fudge factor was tested for 36 different
        /// test cases including prims of type box, sphere, cylinder, and torus,
        /// with varying parameters for sit target location, prim size, prim
        /// rotation, prim cut, prim twist, prim taper, and prim shear. See mantis
        /// issue #1716
        /// </summary>
        private static readonly Vector3 SIT_TARGET_ADJUSTMENT = new Vector3(0.1f, 0.0f, 0.3f);

        public UUID currentParcelUUID = UUID.Zero;

        private ISceneViewer m_sceneViewer;

        /// <value>
        /// The animator for this avatar
        /// </value>
        public ScenePresenceAnimator Animator
        {
            get { return m_animator; }
        }
        protected ScenePresenceAnimator m_animator;

        /// <value>
        /// The scene objects attached to this avatar.  Do not change this list directly - use methods such as
        /// AddAttachment() and RemoveAttachment().  Lock this list when performing any read operations upon it.
        /// </value>
        public List<SceneObjectGroup> Attachments
        {
            get { return m_attachments; }
        }
        protected List<SceneObjectGroup> m_attachments = new List<SceneObjectGroup>();

        private Dictionary<UUID, ScriptControllers> scriptedcontrols = new Dictionary<UUID, ScriptControllers>();
        private ScriptControlled IgnoredControls = ScriptControlled.CONTROL_ZERO;
        private ScriptControlled LastCommands = ScriptControlled.CONTROL_ZERO;
        private bool MouseDown = false;
        private SceneObjectGroup proxyObjectGroup;
        //private SceneObjectPart proxyObjectPart = null;
        public Vector3 lastKnownAllowedPosition;
        public bool sentMessageAboutRestrictedParcelFlyingDown;
        public Vector4 CollisionPlane = Vector4.UnitW;

        private Vector3 m_lastPosition;
        private Quaternion m_lastRotation;
        private Vector3 m_lastVelocity;
        //private int m_lastTerseSent;

        private bool m_updateflag;
        private byte m_movementflag;
        private Vector3? m_forceToApply;
        private uint m_requestedSitTargetID;
        private UUID m_requestedSitTargetUUID;
        public bool SitGround = false;

        private SendCourseLocationsMethod m_sendCourseLocationsMethod;

        private bool m_startAnimationSet;

        //private Vector3 m_requestedSitOffset = new Vector3();

        private Vector3 m_LastFinitePos;

        private float m_sitAvatarHeight = 2.0f;

        private int m_godLevel;
        private int m_userLevel;

        private bool m_invulnerable = true;

        private Vector3 m_lastChildAgentUpdatePosition;
        private Vector3 m_lastChildAgentUpdateCamPosition;

        private int m_perfMonMS;

        private bool m_setAlwaysRun;
        
        private bool m_forceFly;
        private bool m_flyDisabled;

        private float m_speedModifier = 1.0f;

        private Quaternion m_bodyRot= Quaternion.Identity;

        private Quaternion m_bodyRotPrevious = Quaternion.Identity;

        private const int LAND_VELOCITYMAG_MAX = 12;

        public bool IsRestrictedToRegion;

        public string JID = String.Empty;

        private float m_health = 100f;

        // Default AV Height
        private float m_avHeight = 127.0f;

        protected RegionInfo m_regionInfo;
        protected ulong crossingFromRegion;

        private readonly Vector3[] Dir_Vectors = new Vector3[9];

        // Position of agent's camera in world (region cordinates)
        protected Vector3 m_CameraCenter;
        protected Vector3 m_lastCameraCenter;

        protected Timer m_reprioritization_timer;
        protected bool m_reprioritizing;
        protected bool m_reprioritization_called;

        // Use these three vectors to figure out what the agent is looking at
        // Convert it to a Matrix and/or Quaternion
        protected Vector3 m_CameraAtAxis;
        protected Vector3 m_CameraLeftAxis;
        protected Vector3 m_CameraUpAxis;
        private AgentManager.ControlFlags m_AgentControlFlags;
        private Quaternion m_headrotation = Quaternion.Identity;
        private byte m_state;

        //Reuse the Vector3 instead of creating a new one on the UpdateMovement method
//        private Vector3 movementvector;

        private bool m_autopilotMoving;
        private Vector3 m_autoPilotTarget;
        private bool m_sitAtAutoTarget;

        private string m_nextSitAnimation = String.Empty;

        //PauPaw:Proper PID Controler for autopilot************
        private bool m_moveToPositionInProgress;
        private Vector3 m_moveToPositionTarget;

        private bool m_followCamAuto;

        private int m_movementUpdateCount;
        private const int NumMovementsBetweenRayCast = 5;

        private bool CameraConstraintActive;
        //private int m_moveToPositionStateStatus;
        //*****************************************************

        // Agent's Draw distance.
        protected float m_DrawDistance;

        protected AvatarAppearance m_appearance;

        // neighbouring regions we have enabled a child agent in
        // holds the seed cap for the child agent in that region
        private Dictionary<ulong, string> m_knownChildRegions = new Dictionary<ulong, string>();

        /// <summary>
        /// Implemented Control Flags
        /// </summary>
        private enum Dir_ControlFlags
        {
            DIR_CONTROL_FLAG_FORWARD = AgentManager.ControlFlags.AGENT_CONTROL_AT_POS,
            DIR_CONTROL_FLAG_BACK = AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG,
            DIR_CONTROL_FLAG_LEFT = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS,
            DIR_CONTROL_FLAG_RIGHT = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG,
            DIR_CONTROL_FLAG_UP = AgentManager.ControlFlags.AGENT_CONTROL_UP_POS,
            DIR_CONTROL_FLAG_DOWN = AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG,
            DIR_CONTROL_FLAG_FORWARD_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_POS,
            DIR_CONTROL_FLAG_BACKWARD_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_NEG,
            DIR_CONTROL_FLAG_DOWN_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG
        }
        
        /// <summary>
        /// Position at which a significant movement was made
        /// </summary>
        private Vector3 posLastSignificantMove;

        // For teleports and crossings callbacks
        string m_callbackURI;
        UUID m_originRegionID;

        ulong m_rootRegionHandle;

        /// <value>
        /// Script engines present in the scene
        /// </value>
        private IScriptModule[] m_scriptEngines;

        #region Properties

        /// <summary>
        /// Physical scene representation of this Avatar.
        /// </summary>
        public PhysicsActor PhysicsActor
        {
            set { m_physicsActor = value; }
            get { return m_physicsActor; }
        }

        public byte MovementFlag
        {
            set { m_movementflag = value; }
            get { return m_movementflag; }
        }

        public bool Updated
        {
            set { m_updateflag = value; }
            get { return m_updateflag; }
        }

        public bool Invulnerable
        {
            set { m_invulnerable = value; }
            get { return m_invulnerable; }
        }

        public int UserLevel
        {
            get { return m_userLevel; }
        }

        public int GodLevel
        {
            get { return m_godLevel; }
        }

        public ulong RegionHandle
        {
            get { return m_rootRegionHandle; }
        }

        public Vector3 CameraPosition
        {
            get { return m_CameraCenter; }
        }

        public Quaternion CameraRotation
        {
            get { return Util.Axes2Rot(m_CameraAtAxis, m_CameraLeftAxis, m_CameraUpAxis); }
        }

        public Vector3 CameraAtAxis
        {
            get { return m_CameraAtAxis; }
        }

        public Vector3 CameraLeftAxis
        {
            get { return m_CameraLeftAxis; }
        }

        public Vector3 CameraUpAxis
        {
            get { return m_CameraUpAxis; }
        }

        public Vector3 Lookat
        {
            get
            {
                Vector3 a = new Vector3(m_CameraAtAxis.X, m_CameraAtAxis.Y, 0);

                if (a == Vector3.Zero)
                    return a;

                return Util.GetNormalizedVector(a);
            }
        }

        private readonly string m_firstname;

        public string Firstname
        {
            get { return m_firstname; }
        }

        private readonly string m_lastname;

        public string Lastname
        {
            get { return m_lastname; }
        }

        private string m_grouptitle;

        public string Grouptitle
        {
            get { return m_grouptitle; }
            set { m_grouptitle = value; }
        }

        public float DrawDistance
        {
            get { return m_DrawDistance; }
        }

        protected bool m_allowMovement = true;

        public bool AllowMovement
        {
            get { return m_allowMovement; }
            set { m_allowMovement = value; }
        }

        public bool SetAlwaysRun
        {
            get
            {
                if (PhysicsActor != null)
                {
                    return PhysicsActor.SetAlwaysRun;
                }
                else
                {
                    return m_setAlwaysRun;
                }
            }
            set
            {
                m_setAlwaysRun = value;
                if (PhysicsActor != null)
                {
                    PhysicsActor.SetAlwaysRun = value;
                }
            }
        }

        public byte State
        {
            get { return m_state; }
            set { m_state = value; }
        }

        public uint AgentControlFlags
        {
            get { return (uint)m_AgentControlFlags; }
            set { m_AgentControlFlags = (AgentManager.ControlFlags)value; }
        }

        /// <summary>
        /// This works out to be the ClientView object associated with this avatar, or it's client connection manager
        /// </summary>
        private IClientAPI m_controllingClient;

        protected PhysicsActor m_physicsActor;

        /// <value>
        /// The client controlling this presence
        /// </value>
        public IClientAPI ControllingClient
        {
            get { return m_controllingClient; }
        }

        public IClientCore ClientView
        {
            get { return (IClientCore) m_controllingClient; }
        }

        protected Vector3 m_parentPosition;
        public Vector3 ParentPosition
        {
            get { return m_parentPosition; }
            set { m_parentPosition = value; }
        }

        /// <summary>
        /// Position of this avatar relative to the region the avatar is in
        /// </summary>
        public override Vector3 AbsolutePosition
        {
            get
            {
                PhysicsActor actor = m_physicsActor;
                if (actor != null)
                    m_pos = actor.Position;

                return m_parentPosition + m_pos;
            }
            set
            {
                PhysicsActor actor = m_physicsActor;
                if (actor != null)
                {
                    try
                    {
                        lock (m_scene.SyncRoot)
                            m_physicsActor.Position = value;
                    }
                    catch (Exception e)
                    {
                        m_log.Error("[SCENEPRESENCE]: ABSOLUTE POSITION " + e.Message);
                    }
                }

                m_pos = value;
                m_parentPosition = Vector3.Zero;
            }
        }

        public Vector3 OffsetPosition
        {
            get { return m_pos; }
            set { m_pos = value; }
        }

        /// <summary>
        /// Current velocity of the avatar.
        /// </summary>
        public override Vector3 Velocity
        {
            get
            {
                PhysicsActor actor = m_physicsActor;
                if (actor != null)
                    m_velocity = actor.Velocity;

                return m_velocity;
            }
            set
            {
                PhysicsActor actor = m_physicsActor;
                if (actor != null)
                {
                    try
                    {
                        lock (m_scene.SyncRoot)
                            actor.Velocity = value;
                    }
                    catch (Exception e)
                    {
                        m_log.Error("[SCENEPRESENCE]: VELOCITY " + e.Message);
                    }
                }

                m_velocity = value;
            }
        }

        public Quaternion Rotation
        {
            get { return m_bodyRot; }
            set { m_bodyRot = value; }
        }

        public Quaternion PreviousRotation
        {
            get { return m_bodyRotPrevious; }
            set { m_bodyRotPrevious = value; }
        }

        /// <summary>
        /// If this is true, agent doesn't have a representation in this scene.
        ///    this is an agent 'looking into' this scene from a nearby scene(region)
        ///
        /// if False, this agent has a representation in this scene
        /// </summary>
        private bool m_isChildAgent = true;

        public bool IsChildAgent
        {
            get { return m_isChildAgent; }
            set { m_isChildAgent = value; }
        }

        private uint m_parentID;

        public uint ParentID
        {
            get { return m_parentID; }
            set { m_parentID = value; }
        }
        public float Health
        {
            get { return m_health; }
            set { m_health = value; }
        }

        /// <summary>
        /// These are the region handles known by the avatar.
        /// </summary>
        public List<ulong> KnownChildRegionHandles
        {
            get 
            {
                if (m_knownChildRegions.Count == 0) 
                    return new List<ulong>();
                else
                    return new List<ulong>(m_knownChildRegions.Keys); 
            }
        }

        public Dictionary<ulong, string> KnownRegions
        {
            get { return m_knownChildRegions; }
            set 
            {
                m_knownChildRegions = value; 
            }
        }

        public ISceneViewer SceneViewer
        {
            get { return m_sceneViewer; }
        }

        public void AdjustKnownSeeds()
        {
            Dictionary<ulong, string> seeds;

            if (Scene.CapsModule != null)
                seeds = Scene.CapsModule.GetChildrenSeeds(UUID);
            else
                seeds = new Dictionary<ulong, string>();

            List<ulong> old = new List<ulong>();
            foreach (ulong handle in seeds.Keys)
            {
                uint x, y;
                Utils.LongToUInts(handle, out x, out y);
                x = x / Constants.RegionSize;
                y = y / Constants.RegionSize;
                if (Util.IsOutsideView(x, Scene.RegionInfo.RegionLocX, y, Scene.RegionInfo.RegionLocY))
                {
                    old.Add(handle);
                }
            }
            DropOldNeighbours(old);
            
            if (Scene.CapsModule != null)
                Scene.CapsModule.SetChildrenSeed(UUID, seeds);
            
            KnownRegions = seeds;
            //m_log.Debug(" ++++++++++AFTER+++++++++++++ ");
            //DumpKnownRegions();
        }

        public void DumpKnownRegions()
        {
            m_log.Info("================ KnownRegions "+Scene.RegionInfo.RegionName+" ================");
            foreach (KeyValuePair<ulong, string> kvp in KnownRegions)
            {
                uint x, y;
                Utils.LongToUInts(kvp.Key, out x, out y);
                x = x / Constants.RegionSize;
                y = y / Constants.RegionSize;
                m_log.Info(" >> "+x+", "+y+": "+kvp.Value);
            }
        }

        private bool m_inTransit;
        private bool m_mouseLook;
        private bool m_leftButtonDown;

        public bool IsInTransit
        {
            get { return m_inTransit; }
            set { m_inTransit = value; }
        }

        public float SpeedModifier
        {
            get { return m_speedModifier; }
            set { m_speedModifier = value; }
        }

        public bool ForceFly
        {
            get { return m_forceFly; }
            set { m_forceFly = value; }
        }

        public bool FlyDisabled
        {
            get { return m_flyDisabled; }
            set { m_flyDisabled = value; }
        }

        public string Viewer
        {
            get { return m_scene.AuthenticateHandler.GetAgentCircuitData(ControllingClient.CircuitCode).Viewer; }
        }

        #endregion

        #region Constructor(s)
        
        public ScenePresence()
        {
            m_sendCourseLocationsMethod = SendCoarseLocationsDefault;
            CreateSceneViewer();
            m_animator = new ScenePresenceAnimator(this);
        }
        
        private ScenePresence(IClientAPI client, Scene world, RegionInfo reginfo) : this()
        {
            m_rootRegionHandle = reginfo.RegionHandle;
            m_controllingClient = client;
            m_firstname = m_controllingClient.FirstName;
            m_lastname = m_controllingClient.LastName;
            m_name = String.Format("{0} {1}", m_firstname, m_lastname);
            m_scene = world;
            m_uuid = client.AgentId;
            m_regionInfo = reginfo;
            m_localId = m_scene.AllocateLocalId();

            UserAccount account = m_scene.UserAccountService.GetUserAccount(m_scene.RegionInfo.ScopeID, m_uuid);

            if (account != null)
                m_userLevel = account.UserLevel;

            IGroupsModule gm = m_scene.RequestModuleInterface<IGroupsModule>();
            if (gm != null)
                m_grouptitle = gm.GetGroupTitle(m_uuid);

            m_scriptEngines = m_scene.RequestModuleInterfaces<IScriptModule>();
            
            AbsolutePosition = posLastSignificantMove = m_CameraCenter =
                m_lastCameraCenter = m_controllingClient.StartPos;

            m_reprioritization_timer = new Timer(world.ReprioritizationInterval);
            m_reprioritization_timer.Elapsed += new ElapsedEventHandler(Reprioritize);
            m_reprioritization_timer.AutoReset = false;

            AdjustKnownSeeds();

            // TODO: I think, this won't send anything, as we are still a child here...
            Animator.TrySetMovementAnimation("STAND"); 

            // we created a new ScenePresence (a new child agent) in a fresh region.
            // Request info about all the (root) agents in this region
            // Note: This won't send data *to* other clients in that region (children don't send)
            SendInitialFullUpdateToAllClients();

            RegisterToEvents();
            SetDirectionVectors();
        }

        public ScenePresence(IClientAPI client, Scene world, RegionInfo reginfo, byte[] visualParams,
                             AvatarWearable[] wearables)
            : this(client, world, reginfo)
        {
            m_appearance = new AvatarAppearance(m_uuid, wearables, visualParams);
        }

        public ScenePresence(IClientAPI client, Scene world, RegionInfo reginfo, AvatarAppearance appearance)
            : this(client, world, reginfo)
        {
            m_appearance = appearance;
        }

        private void CreateSceneViewer()
        {
            m_sceneViewer = new SceneViewer(this);
        }

        public void RegisterToEvents()
        {
            m_controllingClient.OnRequestWearables += SendWearables;
            m_controllingClient.OnSetAppearance += SetAppearance;
            m_controllingClient.OnCompleteMovementToRegion += CompleteMovement;
            //m_controllingClient.OnCompleteMovementToRegion += SendInitialData;
            m_controllingClient.OnAgentUpdate += HandleAgentUpdate;
            m_controllingClient.OnAgentRequestSit += HandleAgentRequestSit;
            m_controllingClient.OnAgentSit += HandleAgentSit;
            m_controllingClient.OnSetAlwaysRun += HandleSetAlwaysRun;
            m_controllingClient.OnStartAnim += HandleStartAnim;
            m_controllingClient.OnStopAnim += HandleStopAnim;
            m_controllingClient.OnForceReleaseControls += HandleForceReleaseControls;
            m_controllingClient.OnAutoPilotGo += DoAutoPilot;
            m_controllingClient.AddGenericPacketHandler("autopilot", DoMoveToPosition);

            // ControllingClient.OnChildAgentStatus += new StatusChange(this.ChildStatusChange);
            // ControllingClient.OnStopMovement += new GenericCall2(this.StopMovement);
        }

        private void SetDirectionVectors()
        {
            Dir_Vectors[0] = Vector3.UnitX; //FORWARD
            Dir_Vectors[1] = -Vector3.UnitX; //BACK
            Dir_Vectors[2] = Vector3.UnitY; //LEFT
            Dir_Vectors[3] = -Vector3.UnitY; //RIGHT
            Dir_Vectors[4] = Vector3.UnitZ; //UP
            Dir_Vectors[5] = -Vector3.UnitZ; //DOWN
            Dir_Vectors[8] = new Vector3(0f, 0f, -0.5f); //DOWN_Nudge
            Dir_Vectors[6] = Vector3.UnitX*2; //FORWARD
            Dir_Vectors[7] = -Vector3.UnitX; //BACK
        }

        private Vector3[] GetWalkDirectionVectors()
        {
            Vector3[] vector = new Vector3[9];
            vector[0] = new Vector3(m_CameraUpAxis.Z, 0f, -m_CameraAtAxis.Z); //FORWARD
            vector[1] = new Vector3(-m_CameraUpAxis.Z, 0f, m_CameraAtAxis.Z); //BACK
            vector[2] = Vector3.UnitY; //LEFT
            vector[3] = -Vector3.UnitY; //RIGHT
            vector[4] = new Vector3(m_CameraAtAxis.Z, 0f, m_CameraUpAxis.Z); //UP
            vector[5] = new Vector3(-m_CameraAtAxis.Z, 0f, -m_CameraUpAxis.Z); //DOWN
            vector[8] = new Vector3(-m_CameraAtAxis.Z, 0f, -m_CameraUpAxis.Z); //DOWN_Nudge
            vector[6] = (new Vector3(m_CameraUpAxis.Z, 0f, -m_CameraAtAxis.Z) * 2); //FORWARD Nudge
            vector[7] = new Vector3(-m_CameraUpAxis.Z, 0f, m_CameraAtAxis.Z); //BACK Nudge
            return vector;
        }

        #endregion

        public uint GenerateClientFlags(UUID ObjectID)
        {
            return m_scene.Permissions.GenerateClientFlags(m_uuid, ObjectID);
        }

        /// <summary>
        /// Send updates to the client about prims which have been placed on the update queue.  We don't
        /// necessarily send updates for all the parts on the queue, e.g. if an updates with a more recent
        /// timestamp has already been sent.
        /// </summary>
        public void SendPrimUpdates()
        {
            m_perfMonMS = Util.EnvironmentTickCount();

            m_sceneViewer.SendPrimUpdates();

            m_scene.StatsReporter.AddAgentTime(Util.EnvironmentTickCountSubtract(m_perfMonMS));
        }

        #region Status Methods

        /// <summary>
        /// This turns a child agent, into a root agent
        /// This is called when an agent teleports into a region, or if an
        /// agent crosses into this region from a neighbor over the border
        /// </summary>
        public void MakeRootAgent(Vector3 pos, bool isFlying)
        {
            m_log.DebugFormat(
                "[SCENE]: Upgrading child to root agent for {0} in {1}",
                Name, m_scene.RegionInfo.RegionName);

            //m_log.DebugFormat("[SCENE]: known regions in {0}: {1}", Scene.RegionInfo.RegionName, KnownChildRegionHandles.Count);

            IGroupsModule gm = m_scene.RequestModuleInterface<IGroupsModule>();
            if (gm != null)
                m_grouptitle = gm.GetGroupTitle(m_uuid);

            m_rootRegionHandle = m_scene.RegionInfo.RegionHandle;

            m_scene.SetRootAgentScene(m_uuid);

            // Moved this from SendInitialData to ensure that m_appearance is initialized
            // before the inventory is processed in MakeRootAgent. This fixes a race condition
            // related to the handling of attachments
            //m_scene.GetAvatarAppearance(m_controllingClient, out m_appearance);
            if (m_scene.TestBorderCross(pos, Cardinals.E))
            {
                Border crossedBorder = m_scene.GetCrossedBorder(pos, Cardinals.E);
                pos.X = crossedBorder.BorderLine.Z - 1;
            }

            if (m_scene.TestBorderCross(pos, Cardinals.N))
            {
                Border crossedBorder = m_scene.GetCrossedBorder(pos, Cardinals.N);
                pos.Y = crossedBorder.BorderLine.Z - 1;
            }

            if (pos.X < 0f || pos.Y < 0f || pos.Z < 0f)
            {
                m_log.WarnFormat(
                    "[SCENE PRESENCE]: MakeRootAgent() was given an illegal position of {0} for avatar {1}, {2}. Clamping",
                    pos, Name, UUID);

                if (pos.X < 0f) pos.X = 0f;
                if (pos.Y < 0f) pos.Y = 0f;
                if (pos.Z < 0f) pos.Z = 0f;
            }

            float localAVHeight = 1.56f;
            if (m_avHeight != 127.0f)
            {
                localAVHeight = m_avHeight;
            }

            float posZLimit = 0;

            if (pos.X < Constants.RegionSize && pos.Y < Constants.RegionSize)
                posZLimit = (float)m_scene.Heightmap[(int)pos.X, (int)pos.Y];
            
            float newPosZ = posZLimit + localAVHeight / 2;
            if (posZLimit >= (pos.Z - (localAVHeight / 2)) && !(Single.IsInfinity(newPosZ) || Single.IsNaN(newPosZ)))
            {
                pos.Z = newPosZ;
            }
            AbsolutePosition = pos;

            AddToPhysicalScene(isFlying);

            if (m_forceFly)
            {
                m_physicsActor.Flying = true;
            }
            else if (m_flyDisabled)
            {
                m_physicsActor.Flying = false;
            }

            if (m_appearance != null)
            {
                if (m_appearance.AvatarHeight > 0)
                    SetHeight(m_appearance.AvatarHeight);
            }
            else
            {
                m_log.ErrorFormat("[SCENE PRESENCE]: null appearance in MakeRoot in {0}", Scene.RegionInfo.RegionName);
                // emergency; this really shouldn't happen
                m_appearance = new AvatarAppearance(UUID);
            }
            
            // Don't send an animation pack here, since on a region crossing this will sometimes cause a flying 
            // avatar to return to the standing position in mid-air.  On login it looks like this is being sent
            // elsewhere anyway
            // Animator.SendAnimPack();

            m_scene.SwapRootAgentCount(false);
            
            //CachedUserInfo userInfo = m_scene.CommsManager.UserProfileCacheService.GetUserDetails(m_uuid);
            //if (userInfo != null)
            //        userInfo.FetchInventory();
            //else
            //    m_log.ErrorFormat("[SCENE]: Could not find user info for {0} when making it a root agent", m_uuid);
            
            // On the next prim update, all objects will be sent
            //
            m_sceneViewer.Reset();

            m_isChildAgent = false;

            // send the animations of the other presences to me
            m_scene.ForEachScenePresence(delegate(ScenePresence presence)
            {
                if (presence != this)
                    presence.Animator.SendAnimPackToClient(ControllingClient);
            });

            m_scene.EventManager.TriggerOnMakeRootAgent(this);
        }

        /// <summary>
        /// This turns a root agent into a child agent
        /// when an agent departs this region for a neighbor, this gets called.
        ///
        /// It doesn't get called for a teleport.  Reason being, an agent that
        /// teleports out may not end up anywhere near this region
        /// </summary>
        public void MakeChildAgent()
        {
            // It looks like m_animator is set to null somewhere, and MakeChild
            // is called after that. Probably in aborted teleports.
            if (m_animator == null)
                m_animator = new ScenePresenceAnimator(this);
            else
                Animator.ResetAnimations();

//            m_log.DebugFormat(
//                 "[SCENEPRESENCE]: Downgrading root agent {0}, {1} to a child agent in {2}",
//                 Name, UUID, m_scene.RegionInfo.RegionName);

            // Don't zero out the velocity since this can cause problems when an avatar is making a region crossing,
            // depending on the exact timing.  This shouldn't matter anyway since child agent positions are not updated.
            //Velocity = new Vector3(0, 0, 0);
            
            m_isChildAgent = true;
            m_scene.SwapRootAgentCount(true);
            RemoveFromPhysicalScene();

            // FIXME: Set m_rootRegionHandle to the region handle of the scene this agent is moving into
            
            m_scene.EventManager.TriggerOnMakeChildAgent(this);
        }

        /// <summary>
        /// Removes physics plugin scene representation of this agent if it exists.
        /// </summary>
        private void RemoveFromPhysicalScene()
        {
            if (PhysicsActor != null)
            {
                m_physicsActor.OnRequestTerseUpdate -= SendTerseUpdateToAllClients;
                m_physicsActor.OnOutOfBounds -= OutOfBoundsCall;
                m_scene.PhysicsScene.RemoveAvatar(PhysicsActor);
                m_physicsActor.UnSubscribeEvents();
                m_physicsActor.OnCollisionUpdate -= PhysicsCollisionUpdate;
                PhysicsActor = null;
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="pos"></param>
        public void Teleport(Vector3 pos)
        {
            bool isFlying = false;
            if (m_physicsActor != null)
                isFlying = m_physicsActor.Flying;
            
            RemoveFromPhysicalScene();
            Velocity = Vector3.Zero;
            AbsolutePosition = pos;
            AddToPhysicalScene(isFlying);
            if (m_appearance != null)
            {
                if (m_appearance.AvatarHeight > 0)
                    SetHeight(m_appearance.AvatarHeight);
            }

            SendTerseUpdateToAllClients();
        }

        public void TeleportWithMomentum(Vector3 pos)
        {
            bool isFlying = false;
            if (m_physicsActor != null)
                isFlying = m_physicsActor.Flying;

            RemoveFromPhysicalScene();
            AbsolutePosition = pos;
            AddToPhysicalScene(isFlying);
            if (m_appearance != null)
            {
                if (m_appearance.AvatarHeight > 0)
                    SetHeight(m_appearance.AvatarHeight);
            }

            SendTerseUpdateToAllClients();
        }

        /// <summary>
        ///
        /// </summary>
        public void StopMovement()
        {
        }

        public void StopFlying()
        {
            ControllingClient.StopFlying(this);
        }

        public void AddNeighbourRegion(ulong regionHandle, string cap)
        {
            lock (m_knownChildRegions)
            {
                if (!m_knownChildRegions.ContainsKey(regionHandle))
                {
                    uint x, y;
                    Utils.LongToUInts(regionHandle, out x, out y);
                    m_knownChildRegions.Add(regionHandle, cap);
                }
            }
        }

        public void RemoveNeighbourRegion(ulong regionHandle)
        {
            lock (m_knownChildRegions)
            {
                if (m_knownChildRegions.ContainsKey(regionHandle))
                {
                    m_knownChildRegions.Remove(regionHandle);
                   //m_log.Debug(" !!! removing known region {0} in {1}. Count = {2}", regionHandle, Scene.RegionInfo.RegionName, m_knownChildRegions.Count);
                }
            }
        }

        public void DropOldNeighbours(List<ulong> oldRegions)
        {
            foreach (ulong handle in oldRegions)
            {
                RemoveNeighbourRegion(handle);
                Scene.CapsModule.DropChildSeed(UUID, handle);
            }
        }

        public List<ulong> GetKnownRegionList()
        {
            return new List<ulong>(m_knownChildRegions.Keys);
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Sets avatar height in the phyiscs plugin
        /// </summary>
        internal void SetHeight(float height)
        {
            m_avHeight = height;
            if (PhysicsActor != null && !IsChildAgent)
            {
                Vector3 SetSize = new Vector3(0.45f, 0.6f, m_avHeight);
                PhysicsActor.Size = SetSize;
            }
        }

        /// <summary>
        /// Complete Avatar's movement into the region.
        /// This is called upon a very important packet sent from the client,
        /// so it's client-controlled. Never call this method directly.
        /// </summary>
        public void CompleteMovement(IClientAPI client)
        {
            //m_log.Debug("[SCENE PRESENCE]: CompleteMovement");

            Vector3 look = Velocity;
            if ((look.X == 0) && (look.Y == 0) && (look.Z == 0))
            {
                look = new Vector3(0.99f, 0.042f, 0);
            }

            // Prevent teleporting to an underground location
            // (may crash client otherwise)
            //
            Vector3 pos = AbsolutePosition;
            float ground = m_scene.GetGroundHeight(pos.X, pos.Y);
            if (pos.Z < ground + 1.5f)
            {
                pos.Z = ground + 1.5f;
                AbsolutePosition = pos;
            }

            m_isChildAgent = false;
            bool m_flying = ((m_AgentControlFlags & AgentManager.ControlFlags.AGENT_CONTROL_FLY) != 0);
            MakeRootAgent(AbsolutePosition, m_flying);

            if ((m_callbackURI != null) && !m_callbackURI.Equals(""))
            {
                m_log.DebugFormat("[SCENE PRESENCE]: Releasing agent in URI {0}", m_callbackURI);
                Scene.SimulationService.ReleaseAgent(m_originRegionID, UUID, m_callbackURI);
                m_callbackURI = null;
            }

            //m_log.DebugFormat("Completed movement");

            m_controllingClient.MoveAgentIntoRegion(m_regionInfo, AbsolutePosition, look);
            SendInitialData();

            // Create child agents in neighbouring regions
            if (!m_isChildAgent)
            {
                IEntityTransferModule m_agentTransfer = m_scene.RequestModuleInterface<IEntityTransferModule>();
                if (m_agentTransfer != null)
                    m_agentTransfer.EnableChildAgents(this);
                else
                    m_log.DebugFormat("[SCENE PRESENCE]: Unable to create child agents in neighbours, because AgentTransferModule is not active");

                IFriendsModule friendsModule = m_scene.RequestModuleInterface<IFriendsModule>();
                if (friendsModule != null)
                    friendsModule.SendFriendsOnlineIfNeeded(ControllingClient);
            }

        }

        /// <summary>
        /// Callback for the Camera view block check.  Gets called with the results of the camera view block test
        /// hitYN is true when there's something in the way.
        /// </summary>
        /// <param name="hitYN"></param>
        /// <param name="collisionPoint"></param>
        /// <param name="localid"></param>
        /// <param name="distance"></param>
        public void RayCastCameraCallback(bool hitYN, Vector3 collisionPoint, uint localid, float distance, Vector3 pNormal)
        {
            const float POSITION_TOLERANCE = 0.02f;
            const float VELOCITY_TOLERANCE = 0.02f;
            const float ROTATION_TOLERANCE = 0.02f;

            if (m_followCamAuto)
            {
                if (hitYN)
                {
                    CameraConstraintActive = true;
                    //m_log.DebugFormat("[RAYCASTRESULT]: {0}, {1}, {2}, {3}", hitYN, collisionPoint, localid, distance);
                    
                    Vector3 normal = Vector3.Normalize(new Vector3(0f, 0f, collisionPoint.Z) - collisionPoint);
                    ControllingClient.SendCameraConstraint(new Vector4(normal.X, normal.Y, normal.Z, -1 * Vector3.Distance(new Vector3(0,0,collisionPoint.Z),collisionPoint)));
                }
                else
                {
                    if (!m_pos.ApproxEquals(m_lastPosition, POSITION_TOLERANCE) ||
                        !Velocity.ApproxEquals(m_lastVelocity, VELOCITY_TOLERANCE) ||
                        !m_bodyRot.ApproxEquals(m_lastRotation, ROTATION_TOLERANCE))
                    {
                        if (CameraConstraintActive)
                        {
                            ControllingClient.SendCameraConstraint(new Vector4(0f, 0.5f, 0.9f, -3000f));
                            CameraConstraintActive = false;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// This is the event handler for client movement. If a client is moving, this event is triggering.
        /// </summary>
        public void HandleAgentUpdate(IClientAPI remoteClient, AgentUpdateArgs agentData)
        {
            //if (m_isChildAgent)
            //{
            //    // m_log.Debug("DEBUG: HandleAgentUpdate: child agent");
            //    return;
            //}

            m_perfMonMS = Util.EnvironmentTickCount();

            ++m_movementUpdateCount;
            if (m_movementUpdateCount < 1)
                m_movementUpdateCount = 1;

            #region Sanity Checking

            // This is irritating.  Really.
            if (!AbsolutePosition.IsFinite())
            {
                RemoveFromPhysicalScene();
                m_log.Error("[AVATAR]: NonFinite Avatar position detected... Reset Position. Mantis this please. Error #9999902");

                m_pos = m_LastFinitePos;
                if (!m_pos.IsFinite())
                {
                    m_pos.X = 127f;
                    m_pos.Y = 127f;
                    m_pos.Z = 127f;
                    m_log.Error("[AVATAR]: NonFinite Avatar position detected... Reset Position. Mantis this please. Error #9999903");
                }

                AddToPhysicalScene(false);
            }
            else
            {
                m_LastFinitePos = m_pos;
            }

            #endregion Sanity Checking

            #region Inputs

            AgentManager.ControlFlags flags = (AgentManager.ControlFlags)agentData.ControlFlags;
            Quaternion bodyRotation = agentData.BodyRotation;

            // Camera location in world.  We'll need to raytrace
            // from this location from time to time.
            m_CameraCenter = agentData.CameraCenter;
            if (Vector3.Distance(m_lastCameraCenter, m_CameraCenter) >= Scene.RootReprioritizationDistance)
            {
                ReprioritizeUpdates();
                m_lastCameraCenter = m_CameraCenter;
            }

            // Use these three vectors to figure out what the agent is looking at
            // Convert it to a Matrix and/or Quaternion
            m_CameraAtAxis = agentData.CameraAtAxis;
            m_CameraLeftAxis = agentData.CameraLeftAxis;
            m_CameraUpAxis = agentData.CameraUpAxis;

            // The Agent's Draw distance setting
            m_DrawDistance = agentData.Far;

            // Check if Client has camera in 'follow cam' or 'build' mode.
            Vector3 camdif = (Vector3.One * m_bodyRot - Vector3.One * CameraRotation);

            m_followCamAuto = ((m_CameraUpAxis.Z > 0.959f && m_CameraUpAxis.Z < 0.98f)
               && (Math.Abs(camdif.X) < 0.4f && Math.Abs(camdif.Y) < 0.4f)) ? true : false;

            m_mouseLook = (flags & AgentManager.ControlFlags.AGENT_CONTROL_MOUSELOOK) != 0;
            m_leftButtonDown = (flags & AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_DOWN) != 0;

            #endregion Inputs

            if ((flags & AgentManager.ControlFlags.AGENT_CONTROL_STAND_UP) != 0)
            {
                StandUp();
            }

            //m_log.DebugFormat("[FollowCam]: {0}", m_followCamAuto);
            // Raycast from the avatar's head to the camera to see if there's anything blocking the view
            if ((m_movementUpdateCount % NumMovementsBetweenRayCast) == 0 && m_scene.PhysicsScene.SupportsRayCast())
            {
                if (m_followCamAuto)
                {
                    Vector3 posAdjusted = m_pos + HEAD_ADJUSTMENT;
                    m_scene.PhysicsScene.RaycastWorld(m_pos, Vector3.Normalize(m_CameraCenter - posAdjusted), Vector3.Distance(m_CameraCenter, posAdjusted) + 0.3f, RayCastCameraCallback);
                }
            }

            lock (scriptedcontrols)
            {
                if (scriptedcontrols.Count > 0)
                {
                    SendControlToScripts((uint)flags);
                    flags = RemoveIgnoredControls(flags, IgnoredControls);
                }
            }

            if (m_autopilotMoving)
                CheckAtSitTarget();

            if ((flags & AgentManager.ControlFlags.AGENT_CONTROL_SIT_ON_GROUND) != 0)
            {
                // TODO: This doesn't prevent the user from walking yet.
                // Setting parent ID would fix this, if we knew what value
                // to use.  Or we could add a m_isSitting variable.
                //Animator.TrySetMovementAnimation("SIT_GROUND_CONSTRAINED");
                SitGround = true;                
            }

            // In the future, these values might need to go global.
            // Here's where you get them.
            m_AgentControlFlags = flags;
            m_headrotation = agentData.HeadRotation;
            m_state = agentData.State;

            PhysicsActor actor = PhysicsActor;
            if (actor == null)
            {
                return;
            }
            
            bool update_movementflag = false;

            if (m_allowMovement && !SitGround)
            {
                if (agentData.UseClientAgentPosition)
                {
                    m_moveToPositionInProgress = (agentData.ClientAgentPosition - AbsolutePosition).Length() > 0.2f;
                    m_moveToPositionTarget = agentData.ClientAgentPosition;
                }

                int i = 0;
                
                bool update_rotation = false;
                bool DCFlagKeyPressed = false;
                Vector3 agent_control_v3 = Vector3.Zero;
                Quaternion q = bodyRotation;

                bool oldflying = PhysicsActor.Flying;

                if (m_forceFly)
                    actor.Flying = true;
                else if (m_flyDisabled)
                    actor.Flying = false;
                else
                    actor.Flying = ((flags & AgentManager.ControlFlags.AGENT_CONTROL_FLY) != 0);

                if (actor.Flying != oldflying)
                    update_movementflag = true;

                if (q != m_bodyRot)
                {
                    m_bodyRot = q;
                    update_rotation = true;
                }

                if (m_parentID == 0)
                {
                    bool bAllowUpdateMoveToPosition = false;
                    bool bResetMoveToPosition = false;

                    Vector3[] dirVectors;

                    // use camera up angle when in mouselook and not flying or when holding the left mouse button down and not flying
                    // this prevents 'jumping' in inappropriate situations.
                    if ((m_mouseLook && !m_physicsActor.Flying) || (m_leftButtonDown && !m_physicsActor.Flying))
                        dirVectors = GetWalkDirectionVectors();
                    else
                        dirVectors = Dir_Vectors;

                    // The fact that m_movementflag is a byte needs to be fixed
                    // it really should be a uint
                    uint nudgehack = 250;
                    foreach (Dir_ControlFlags DCF in DIR_CONTROL_FLAGS)
                    {
                        if (((uint)flags & (uint)DCF) != 0)
                        {
                            bResetMoveToPosition = true;
                            DCFlagKeyPressed = true;
                            try
                            {
                                agent_control_v3 += dirVectors[i];
                                //m_log.DebugFormat("[Motion]: {0}, {1}",i, dirVectors[i]);
                            }
                            catch (IndexOutOfRangeException)
                            {
                                // Why did I get this?
                            }

                            if ((m_movementflag & (byte)(uint)DCF) == 0)
                            {
                                if (DCF == Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD_NUDGE || DCF == Dir_ControlFlags.DIR_CONTROL_FLAG_BACKWARD_NUDGE)
                                {
                                    m_movementflag |= (byte)nudgehack;
                                }
                                m_movementflag += (byte)(uint)DCF;
                                update_movementflag = true;
                            }
                        }
                        else
                        {
                            if ((m_movementflag & (byte)(uint)DCF) != 0 ||
                                ((DCF == Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD_NUDGE || DCF == Dir_ControlFlags.DIR_CONTROL_FLAG_BACKWARD_NUDGE)
                                && ((m_movementflag & (byte)nudgehack) == nudgehack))
                                ) // This or is for Nudge forward
                            {
                                m_movementflag -= ((byte)(uint)DCF);

                                update_movementflag = true;
                                /*
                                    if ((DCF == Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD_NUDGE || DCF == Dir_ControlFlags.DIR_CONTROL_FLAG_BACKWARD_NUDGE)
                                    && ((m_movementflag & (byte)nudgehack) == nudgehack))
                                    {
                                        m_log.Debug("Removed Hack flag");
                                    }
                                */
                            }
                            else
                            {
                                bAllowUpdateMoveToPosition = true;
                            }
                        }
                        i++;
                    }

                    //Paupaw:Do Proper PID for Autopilot here
                    if (bResetMoveToPosition)
                    {
                        m_moveToPositionTarget = Vector3.Zero;
                        m_moveToPositionInProgress = false;
                        update_movementflag = true;
                        bAllowUpdateMoveToPosition = false;
                    }

                    if (bAllowUpdateMoveToPosition && (m_moveToPositionInProgress && !m_autopilotMoving))
                    {
                        //Check the error term of the current position in relation to the target position
                        if (Util.GetDistanceTo(AbsolutePosition, m_moveToPositionTarget) <= 0.5f)
                        {
                            // we are close enough to the target
                            m_moveToPositionTarget = Vector3.Zero;
                            m_moveToPositionInProgress = false;
                            update_movementflag = true;
                        }
                        else
                        {
                            try
                            {
                                // move avatar in 2D at one meter/second towards target, in avatar coordinate frame.
                                // This movement vector gets added to the velocity through AddNewMovement().
                                // Theoretically we might need a more complex PID approach here if other 
                                // unknown forces are acting on the avatar and we need to adaptively respond
                                // to such forces, but the following simple approach seems to works fine.
                                Vector3 LocalVectorToTarget3D =
                                    (m_moveToPositionTarget - AbsolutePosition) // vector from cur. pos to target in global coords
                                    * Matrix4.CreateFromQuaternion(Quaternion.Inverse(bodyRotation)); // change to avatar coords
                                // Ignore z component of vector
                                Vector3 LocalVectorToTarget2D = new Vector3((float)(LocalVectorToTarget3D.X), (float)(LocalVectorToTarget3D.Y), 0f);
                                LocalVectorToTarget2D.Normalize();
                                agent_control_v3 += LocalVectorToTarget2D;

                                // update avatar movement flags. the avatar coordinate system is as follows:
                                //
                                //                        +X (forward)
                                //
                                //                        ^
                                //                        |
                                //                        |
                                //                        |
                                //                        |
                                //     (left) +Y <--------o--------> -Y
                                //                       avatar
                                //                        |
                                //                        |
                                //                        |
                                //                        |
                                //                        v
                                //                        -X
                                //

                                // based on the above avatar coordinate system, classify the movement into 
                                // one of left/right/back/forward.
                                if (LocalVectorToTarget2D.Y > 0)//MoveLeft
                                {
                                    m_movementflag += (byte)(uint)Dir_ControlFlags.DIR_CONTROL_FLAG_LEFT;
                                    //AgentControlFlags
                                    AgentControlFlags |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_LEFT;
                                    update_movementflag = true;
                                }
                                else if (LocalVectorToTarget2D.Y < 0) //MoveRight
                                {
                                    m_movementflag += (byte)(uint)Dir_ControlFlags.DIR_CONTROL_FLAG_RIGHT;
                                    AgentControlFlags |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_RIGHT;
                                    update_movementflag = true;
                                }
                                if (LocalVectorToTarget2D.X < 0) //MoveBack
                                {
                                    m_movementflag += (byte)(uint)Dir_ControlFlags.DIR_CONTROL_FLAG_BACK;
                                    AgentControlFlags |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_BACK;
                                    update_movementflag = true;
                                }
                                else if (LocalVectorToTarget2D.X > 0) //Move Forward
                                {
                                    m_movementflag += (byte)(uint)Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD;
                                    AgentControlFlags |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD;
                                    update_movementflag = true;
                                }
                            }
                            catch (Exception e)
                            {
                                //Avoid system crash, can be slower but...
                                m_log.DebugFormat("Crash! {0}", e.ToString());
                            }
                        }
                    }
                }

                // Cause the avatar to stop flying if it's colliding
                // with something with the down arrow pressed.

                // Only do this if we're flying
                if (m_physicsActor != null && m_physicsActor.Flying && !m_forceFly)
                {
                    // Landing detection code

                    // Are the landing controls requirements filled?
                    bool controlland = (((flags & AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) != 0) ||
                                        ((flags & AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG) != 0));

                    // Are the collision requirements fulfilled?
                    bool colliding = (m_physicsActor.IsColliding == true);

                    if (m_physicsActor.Flying && colliding && controlland)
                    {
                        // nesting this check because LengthSquared() is expensive and we don't 
                        // want to do it every step when flying.
                        if ((Velocity.LengthSquared() <= LAND_VELOCITYMAG_MAX))
                            StopFlying();
                    }
                }

                if (update_movementflag || (update_rotation && DCFlagKeyPressed))
                {
                    //                    m_log.DebugFormat("{0} {1}", update_movementflag, (update_rotation && DCFlagKeyPressed));
                    //                    m_log.DebugFormat(
                    //                        "In {0} adding velocity to {1} of {2}", m_scene.RegionInfo.RegionName, Name, agent_control_v3);

                    AddNewMovement(agent_control_v3, q);

                    
                }
            }

            if (update_movementflag && ((flags & AgentManager.ControlFlags.AGENT_CONTROL_SIT_ON_GROUND) == 0) && (m_parentID == 0) && !SitGround)
                Animator.UpdateMovementAnimations();

            m_scene.EventManager.TriggerOnClientMovement(this);

            m_scene.StatsReporter.AddAgentTime(Util.EnvironmentTickCountSubtract(m_perfMonMS));
        }

        public void DoAutoPilot(uint not_used, Vector3 Pos, IClientAPI remote_client)
        {
            m_autopilotMoving = true;
            m_autoPilotTarget = Pos;
            m_sitAtAutoTarget = false;
            PrimitiveBaseShape proxy = PrimitiveBaseShape.Default;
            //proxy.PCode = (byte)PCode.ParticleSystem;

            proxyObjectGroup = new SceneObjectGroup(UUID, Pos, Rotation, proxy);
            proxyObjectGroup.AttachToScene(m_scene);
            
            // Commented out this code since it could never have executed, but might still be informative.
//            if (proxyObjectGroup != null)
//            {
                proxyObjectGroup.SendGroupFullUpdate();
                remote_client.SendSitResponse(proxyObjectGroup.UUID, Vector3.Zero, Quaternion.Identity, true, Vector3.Zero, Vector3.Zero, false);
                m_scene.DeleteSceneObject(proxyObjectGroup, false);
//            }
//            else
//            {
//                m_autopilotMoving = false;
//                m_autoPilotTarget = Vector3.Zero;
//                ControllingClient.SendAlertMessage("Autopilot cancelled");
//            }
        }

        public void DoMoveToPosition(Object sender, string method, List<String> args)
        {
            try
            {
                float locx = 0f;
                float locy = 0f;
                float locz = 0f;
                uint regionX = 0;
                uint regionY = 0;
                try
                {
                    Utils.LongToUInts(Scene.RegionInfo.RegionHandle, out regionX, out regionY);
                    locx = Convert.ToSingle(args[0]) - (float)regionX;
                    locy = Convert.ToSingle(args[1]) - (float)regionY;
                    locz = Convert.ToSingle(args[2]);
                }
                catch (InvalidCastException)
                {
                    m_log.Error("[CLIENT]: Invalid autopilot request");
                    return;
                }
                m_moveToPositionInProgress = true;
                m_moveToPositionTarget = new Vector3(locx, locy, locz);
            }
            catch (Exception ex)
            {
                //Why did I get this error?
               m_log.Error("[SCENEPRESENCE]: DoMoveToPosition" + ex);
            }
        }

        private void CheckAtSitTarget()
        {
            //m_log.Debug("[AUTOPILOT]: " + Util.GetDistanceTo(AbsolutePosition, m_autoPilotTarget).ToString());
            if (Util.GetDistanceTo(AbsolutePosition, m_autoPilotTarget) <= 1.5)
            {
                if (m_sitAtAutoTarget)
                {
                    SceneObjectPart part = m_scene.GetSceneObjectPart(m_requestedSitTargetUUID);
                    if (part != null)
                    {
                        AbsolutePosition = part.AbsolutePosition;
                        Velocity = Vector3.Zero;
                        SendFullUpdateToAllClients();

                        //HandleAgentSit(ControllingClient, m_requestedSitTargetUUID);
                    }
                    //ControllingClient.SendSitResponse(m_requestedSitTargetID, m_requestedSitOffset, Quaternion.Identity, false, Vector3.Zero, Vector3.Zero, false);
                    m_requestedSitTargetUUID = UUID.Zero;
                }
                    /*
                else
                {
                    //ControllingClient.SendAlertMessage("Autopilot cancelled");
                    //SendTerseUpdateToAllClients();
                    //PrimitiveBaseShape proxy = PrimitiveBaseShape.Default;
                    //proxy.PCode = (byte)PCode.ParticleSystem;
                    ////uint nextUUID = m_scene.NextLocalId;

                    //proxyObjectGroup = new SceneObjectGroup(m_scene, m_scene.RegionInfo.RegionHandle, UUID, nextUUID, m_autoPilotTarget, Quaternion.Identity, proxy);
                    //if (proxyObjectGroup != null)
                    //{
                        //proxyObjectGroup.SendGroupFullUpdate();
                        //ControllingClient.SendSitResponse(UUID.Zero, m_autoPilotTarget, Quaternion.Identity, true, Vector3.Zero, Vector3.Zero, false);
                        //m_scene.DeleteSceneObject(proxyObjectGroup);
                    //}
                }
                */
                m_autoPilotTarget = Vector3.Zero;
                m_autopilotMoving = false;
            }
        }
        /// <summary>
        /// Perform the logic necessary to stand the avatar up.  This method also executes
        /// the stand animation.
        /// </summary>
        public void StandUp()
        {
            SitGround = false;

            if (m_parentID != 0)
            {
                m_log.Debug("StandupCode Executed");
                SceneObjectPart part = m_scene.GetSceneObjectPart(m_parentID);
                if (part != null)
                {
                    TaskInventoryDictionary taskIDict = part.TaskInventory;
                    if (taskIDict != null)
                    {
                        lock (taskIDict)
                        {
                            foreach (UUID taskID in taskIDict.Keys)
                            {
                                UnRegisterControlEventsToScript(LocalId, taskID);
                                taskIDict[taskID].PermsMask &= ~(
                                    2048 | //PERMISSION_CONTROL_CAMERA
                                    4); // PERMISSION_TAKE_CONTROLS
                            }
                        }

                    }
                    // Reset sit target.
                    if (part.GetAvatarOnSitTarget() == UUID)
                        part.SetAvatarOnSitTarget(UUID.Zero);

                    m_parentPosition = part.GetWorldPosition();
                    ControllingClient.SendClearFollowCamProperties(part.ParentUUID);
                }

                if (m_physicsActor == null)
                {
                    AddToPhysicalScene(false);
                }

                m_pos += m_parentPosition + new Vector3(0.0f, 0.0f, 2.0f*m_sitAvatarHeight);
                m_parentPosition = Vector3.Zero;

                m_parentID = 0;
                SendFullUpdateToAllClients();
                m_requestedSitTargetID = 0;
                if ((m_physicsActor != null) && (m_avHeight > 0))
                {
                    SetHeight(m_avHeight);
                }
            }

            Animator.TrySetMovementAnimation("STAND");
        }

        private SceneObjectPart FindNextAvailableSitTarget(UUID targetID)
        {
            SceneObjectPart targetPart = m_scene.GetSceneObjectPart(targetID);
            if (targetPart == null)
                return null;

            // If the primitive the player clicked on has a sit target and that sit target is not full, that sit target is used.
            // If the primitive the player clicked on has no sit target, and one or more other linked objects have sit targets that are not full, the sit target of the object with the lowest link number will be used.

            // Get our own copy of the part array, and sort into the order we want to test
            SceneObjectPart[] partArray = targetPart.ParentGroup.GetParts();
            Array.Sort(partArray, delegate(SceneObjectPart p1, SceneObjectPart p2)
                       {
                           // we want the originally selected part first, then the rest in link order -- so make the selected part link num (-1)
                           int linkNum1 = p1==targetPart ? -1 : p1.LinkNum;
                           int linkNum2 = p2==targetPart ? -1 : p2.LinkNum;
                           return linkNum1 - linkNum2;
                       }
                );

            //look for prims with explicit sit targets that are available
            foreach (SceneObjectPart part in partArray)
            {
                // Is a sit target available?
                Vector3 avSitOffSet = part.SitTargetPosition;
                Quaternion avSitOrientation = part.SitTargetOrientation;
                UUID avOnTargetAlready = part.GetAvatarOnSitTarget();

                bool SitTargetUnOccupied = (!(avOnTargetAlready != UUID.Zero));
                bool SitTargetisSet =
                    (!(avSitOffSet.X == 0f && avSitOffSet.Y == 0f && avSitOffSet.Z == 0f && avSitOrientation.W == 1f &&
                       avSitOrientation.X == 0f && avSitOrientation.Y == 0f && avSitOrientation.Z == 0f));

                if (SitTargetisSet && SitTargetUnOccupied)
                {
                    //switch the target to this prim
                    return part;
                }
            }

            // no explicit sit target found - use original target
            return targetPart;
        }

        private void SendSitResponse(IClientAPI remoteClient, UUID targetID, Vector3 offset, Quaternion pSitOrientation)
        {
            bool autopilot = true;
            Vector3 pos = new Vector3();
            Quaternion sitOrientation = pSitOrientation;
            Vector3 cameraEyeOffset = Vector3.Zero;
            Vector3 cameraAtOffset = Vector3.Zero;
            bool forceMouselook = false;

            //SceneObjectPart part =  m_scene.GetSceneObjectPart(targetID);
            SceneObjectPart part =  FindNextAvailableSitTarget(targetID);
            if (part != null)
            {
                // TODO: determine position to sit at based on scene geometry; don't trust offset from client
                // see http://wiki.secondlife.com/wiki/User:Andrew_Linden/Office_Hours/2007_11_06 for details on how LL does it

                // Is a sit target available?
                Vector3 avSitOffSet = part.SitTargetPosition;
                Quaternion avSitOrientation = part.SitTargetOrientation;
                UUID avOnTargetAlready = part.GetAvatarOnSitTarget();

                bool SitTargetUnOccupied = (!(avOnTargetAlready != UUID.Zero));
                bool SitTargetisSet =
                    (!(avSitOffSet.X == 0f && avSitOffSet.Y == 0f && avSitOffSet.Z == 0f &&
                       (
                           avSitOrientation.X == 0f && avSitOrientation.Y == 0f && avSitOrientation.Z == 0f && avSitOrientation.W == 1f // Valid Zero Rotation quaternion
                           || avSitOrientation.X == 0f && avSitOrientation.Y == 0f && avSitOrientation.Z == 1f && avSitOrientation.W == 0f // W-Z Mapping was invalid at one point
                           || avSitOrientation.X == 0f && avSitOrientation.Y == 0f && avSitOrientation.Z == 0f && avSitOrientation.W == 0f // Invalid Quaternion
                       )
                       ));

                if (SitTargetisSet && SitTargetUnOccupied)
                {
                    part.SetAvatarOnSitTarget(UUID);
                    offset = new Vector3(avSitOffSet.X, avSitOffSet.Y, avSitOffSet.Z);
                    sitOrientation = avSitOrientation;
                    autopilot = false;
                }

                pos = part.AbsolutePosition + offset;
                //if (Math.Abs(part.AbsolutePosition.Z - AbsolutePosition.Z) > 1)
                //{
                   // offset = pos;
                    //autopilot = false;
                //}
                if (m_physicsActor != null)
                {
                    // If we're not using the client autopilot, we're immediately warping the avatar to the location
                    // We can remove the physicsActor until they stand up.
                    m_sitAvatarHeight = m_physicsActor.Size.Z;

                    if (autopilot)
                    {
                        if (Util.GetDistanceTo(AbsolutePosition, pos) < 4.5)
                        {
                            autopilot = false;

                            RemoveFromPhysicalScene();
                            AbsolutePosition = pos + new Vector3(0.0f, 0.0f, m_sitAvatarHeight);
                        }
                    }
                    else
                    {
                        RemoveFromPhysicalScene();
                    }
                }

                cameraAtOffset = part.GetCameraAtOffset();
                cameraEyeOffset = part.GetCameraEyeOffset();
                forceMouselook = part.GetForceMouselook();
            }

            ControllingClient.SendSitResponse(targetID, offset, sitOrientation, autopilot, cameraAtOffset, cameraEyeOffset, forceMouselook);
            m_requestedSitTargetUUID = targetID;
            // This calls HandleAgentSit twice, once from here, and the client calls
            // HandleAgentSit itself after it gets to the location
            // It doesn't get to the location until we've moved them there though
            // which happens in HandleAgentSit :P
            m_autopilotMoving = autopilot;
            m_autoPilotTarget = pos;
            m_sitAtAutoTarget = autopilot;
            if (!autopilot)
                HandleAgentSit(remoteClient, UUID);
        }

        public void HandleAgentRequestSit(IClientAPI remoteClient, UUID agentID, UUID targetID, Vector3 offset)
        {
            if (m_parentID != 0)
            {
                StandUp();
            }
            m_nextSitAnimation = "SIT";
            
            //SceneObjectPart part = m_scene.GetSceneObjectPart(targetID);
            SceneObjectPart part = FindNextAvailableSitTarget(targetID);

            if (part != null)
            {
                if (!String.IsNullOrEmpty(part.SitAnimation))
                {
                    m_nextSitAnimation = part.SitAnimation;
                }
                m_requestedSitTargetID = part.LocalId;
                //m_requestedSitOffset = offset;
                m_requestedSitTargetUUID = targetID;
                
                m_log.DebugFormat("[SIT]: Client requested Sit Position: {0}", offset);
                
                if (m_scene.PhysicsScene.SupportsRayCast())
                {
                    //m_scene.PhysicsScene.RaycastWorld(Vector3.Zero,Vector3.Zero, 0.01f,new RaycastCallback());
                    //SitRayCastAvatarPosition(part);
                    //return;
                }
            }
            else
            {
                
                m_log.Warn("Sit requested on unknown object: " + targetID.ToString());
            }

            

            SendSitResponse(remoteClient, targetID, offset, Quaternion.Identity);
        }
        /*
        public void SitRayCastAvatarPosition(SceneObjectPart part)
        {
            Vector3 EndRayCastPosition = part.AbsolutePosition + m_requestedSitOffset;
            Vector3 StartRayCastPosition = AbsolutePosition;
            Vector3 direction = Vector3.Normalize(EndRayCastPosition - StartRayCastPosition);
            float distance = Vector3.Distance(EndRayCastPosition, StartRayCastPosition);
            m_scene.PhysicsScene.RaycastWorld(StartRayCastPosition, direction, distance, SitRayCastAvatarPositionResponse);
        }

        public void SitRayCastAvatarPositionResponse(bool hitYN, Vector3 collisionPoint, uint localid, float pdistance, Vector3 normal)
        {
            SceneObjectPart part =  FindNextAvailableSitTarget(m_requestedSitTargetUUID);
            if (part != null)
            {
                if (hitYN)
                {
                    if (collisionPoint.ApproxEquals(m_requestedSitOffset + part.AbsolutePosition, 0.2f))
                    {
                        SitRaycastFindEdge(collisionPoint, normal);
                        m_log.DebugFormat("[SIT]: Raycast Avatar Position succeeded at point: {0}, normal:{1}", collisionPoint, normal);
                    }
                    else
                    {
                        SitRayCastAvatarPositionCameraZ(part);
                    }
                }
                else
                {
                    SitRayCastAvatarPositionCameraZ(part);
                }
            }
            else
            {
                ControllingClient.SendAlertMessage("Sit position no longer exists");
                m_requestedSitTargetUUID = UUID.Zero;
                m_requestedSitTargetID = 0;
                m_requestedSitOffset = Vector3.Zero;
            }

        }

        public void SitRayCastAvatarPositionCameraZ(SceneObjectPart part)
        {
            // Next, try to raycast from the camera Z position
            Vector3 EndRayCastPosition = part.AbsolutePosition + m_requestedSitOffset;
            Vector3 StartRayCastPosition = AbsolutePosition; StartRayCastPosition.Z = CameraPosition.Z;
            Vector3 direction = Vector3.Normalize(EndRayCastPosition - StartRayCastPosition);
            float distance = Vector3.Distance(EndRayCastPosition, StartRayCastPosition);
            m_scene.PhysicsScene.RaycastWorld(StartRayCastPosition, direction, distance, SitRayCastAvatarPositionCameraZResponse);
        }

        public void SitRayCastAvatarPositionCameraZResponse(bool hitYN, Vector3 collisionPoint, uint localid, float pdistance, Vector3 normal)
        {
            SceneObjectPart part = FindNextAvailableSitTarget(m_requestedSitTargetUUID);
            if (part != null)
            {
                if (hitYN)
                {
                    if (collisionPoint.ApproxEquals(m_requestedSitOffset + part.AbsolutePosition, 0.2f))
                    {
                        SitRaycastFindEdge(collisionPoint, normal);
                        m_log.DebugFormat("[SIT]: Raycast Avatar Position + CameraZ succeeded at point: {0}, normal:{1}", collisionPoint, normal);
                    }
                    else
                    {
                        SitRayCastCameraPosition(part);
                    }
                }
                else
                {
                    SitRayCastCameraPosition(part);
                }
            }
            else
            {
                ControllingClient.SendAlertMessage("Sit position no longer exists");
                m_requestedSitTargetUUID = UUID.Zero;
                m_requestedSitTargetID = 0;
                m_requestedSitOffset = Vector3.Zero;
            }

        }

        public void SitRayCastCameraPosition(SceneObjectPart part)
        {
            // Next, try to raycast from the camera position
            Vector3 EndRayCastPosition = part.AbsolutePosition + m_requestedSitOffset;
            Vector3 StartRayCastPosition = CameraPosition;
            Vector3 direction = Vector3.Normalize(EndRayCastPosition - StartRayCastPosition);
            float distance = Vector3.Distance(EndRayCastPosition, StartRayCastPosition);
            m_scene.PhysicsScene.RaycastWorld(StartRayCastPosition, direction, distance, SitRayCastCameraPositionResponse);
        }

        public void SitRayCastCameraPositionResponse(bool hitYN, Vector3 collisionPoint, uint localid, float pdistance, Vector3 normal)
        {
            SceneObjectPart part = FindNextAvailableSitTarget(m_requestedSitTargetUUID);
            if (part != null)
            {
                if (hitYN)
                {
                    if (collisionPoint.ApproxEquals(m_requestedSitOffset + part.AbsolutePosition, 0.2f))
                    {
                        SitRaycastFindEdge(collisionPoint, normal);
                        m_log.DebugFormat("[SIT]: Raycast Camera Position succeeded at point: {0}, normal:{1}", collisionPoint, normal);
                    }
                    else
                    {
                        SitRayHorizontal(part);
                    }
                }
                else
                {
                    SitRayHorizontal(part);
                }
            }
            else
            {
                ControllingClient.SendAlertMessage("Sit position no longer exists");
                m_requestedSitTargetUUID = UUID.Zero;
                m_requestedSitTargetID = 0;
                m_requestedSitOffset = Vector3.Zero;
            }

        }

        public void SitRayHorizontal(SceneObjectPart part)
        {
            // Next, try to raycast from the avatar position to fwd
            Vector3 EndRayCastPosition = part.AbsolutePosition + m_requestedSitOffset;
            Vector3 StartRayCastPosition = CameraPosition;
            Vector3 direction = Vector3.Normalize(EndRayCastPosition - StartRayCastPosition);
            float distance = Vector3.Distance(EndRayCastPosition, StartRayCastPosition);
            m_scene.PhysicsScene.RaycastWorld(StartRayCastPosition, direction, distance, SitRayCastHorizontalResponse);
        }

        public void SitRayCastHorizontalResponse(bool hitYN, Vector3 collisionPoint, uint localid, float pdistance, Vector3 normal)
        {
            SceneObjectPart part = FindNextAvailableSitTarget(m_requestedSitTargetUUID);
            if (part != null)
            {
                if (hitYN)
                {
                    if (collisionPoint.ApproxEquals(m_requestedSitOffset + part.AbsolutePosition, 0.2f))
                    {
                        SitRaycastFindEdge(collisionPoint, normal);
                        m_log.DebugFormat("[SIT]: Raycast Horizontal Position succeeded at point: {0}, normal:{1}", collisionPoint, normal);
                        // Next, try to raycast from the camera position
                        Vector3 EndRayCastPosition = part.AbsolutePosition + m_requestedSitOffset;
                        Vector3 StartRayCastPosition = CameraPosition;
                        Vector3 direction = Vector3.Normalize(EndRayCastPosition - StartRayCastPosition);
                        float distance = Vector3.Distance(EndRayCastPosition, StartRayCastPosition);
                        //m_scene.PhysicsScene.RaycastWorld(StartRayCastPosition, direction, distance, SitRayCastResponseAvatarPosition);
                    }
                    else
                    {
                        ControllingClient.SendAlertMessage("Sit position not accessable.");
                        m_requestedSitTargetUUID = UUID.Zero;
                        m_requestedSitTargetID = 0;
                        m_requestedSitOffset = Vector3.Zero;
                    }
                }
                else
                {
                    ControllingClient.SendAlertMessage("Sit position not accessable.");
                    m_requestedSitTargetUUID = UUID.Zero;
                    m_requestedSitTargetID = 0;
                    m_requestedSitOffset = Vector3.Zero;
                }
            }
            else
            {
                ControllingClient.SendAlertMessage("Sit position no longer exists");
                m_requestedSitTargetUUID = UUID.Zero;
                m_requestedSitTargetID = 0;
                m_requestedSitOffset = Vector3.Zero;
            }

        }

        private void SitRaycastFindEdge(Vector3 collisionPoint, Vector3 collisionNormal)
        {
            int i = 0;
            //throw new NotImplementedException();
            //m_requestedSitTargetUUID = UUID.Zero;
            //m_requestedSitTargetID = 0;
            //m_requestedSitOffset = Vector3.Zero;

            SendSitResponse(ControllingClient, m_requestedSitTargetUUID, collisionPoint - m_requestedSitOffset, Quaternion.Identity);
        }
        */
        public void HandleAgentRequestSit(IClientAPI remoteClient, UUID agentID, UUID targetID, Vector3 offset, string sitAnimation)
        {
            if (m_parentID != 0)
            {
                StandUp();
            }
            if (!String.IsNullOrEmpty(sitAnimation))
            {
                m_nextSitAnimation = sitAnimation;
            }
            else
            {
                m_nextSitAnimation = "SIT";
            }

            //SceneObjectPart part = m_scene.GetSceneObjectPart(targetID);
            SceneObjectPart part =  FindNextAvailableSitTarget(targetID);
            if (part != null)
            {
                m_requestedSitTargetID = part.LocalId; 
                //m_requestedSitOffset = offset;
                m_requestedSitTargetUUID = targetID;

                m_log.DebugFormat("[SIT]: Client requested Sit Position: {0}", offset);

                if (m_scene.PhysicsScene.SupportsRayCast())
                {
                    //SitRayCastAvatarPosition(part);
                    //return;
                }
            }
            else
            {
                m_log.Warn("Sit requested on unknown object: " + targetID);
            }
            
            SendSitResponse(remoteClient, targetID, offset, Quaternion.Identity);
        }

        public void HandleAgentSit(IClientAPI remoteClient, UUID agentID)
        {
            if (!String.IsNullOrEmpty(m_nextSitAnimation))
            {
                HandleAgentSit(remoteClient, agentID, m_nextSitAnimation);
            }
            else
            {
                HandleAgentSit(remoteClient, agentID, "SIT");
            }
        }
        
        public void HandleAgentSit(IClientAPI remoteClient, UUID agentID, string sitAnimation)
        {
            SceneObjectPart part = m_scene.GetSceneObjectPart(m_requestedSitTargetID);

            if (m_sitAtAutoTarget || !m_autopilotMoving)
            {
                if (part != null)
                {
                    if (part.GetAvatarOnSitTarget() == UUID)
                    {
                        Vector3 sitTargetPos = part.SitTargetPosition;
                        Quaternion sitTargetOrient = part.SitTargetOrientation;

                        //Quaternion vq = new Quaternion(sitTargetPos.X, sitTargetPos.Y+0.2f, sitTargetPos.Z+0.2f, 0);
                        //Quaternion nq = new Quaternion(-sitTargetOrient.X, -sitTargetOrient.Y, -sitTargetOrient.Z, sitTargetOrient.w);

                        //Quaternion result = (sitTargetOrient * vq) * nq;

                        m_pos = new Vector3(sitTargetPos.X, sitTargetPos.Y, sitTargetPos.Z);
                        m_pos += SIT_TARGET_ADJUSTMENT;
                        m_bodyRot = sitTargetOrient;
                        //Rotation = sitTargetOrient;
                        m_parentPosition = part.AbsolutePosition;

                        //SendTerseUpdateToAllClients();
                    }
                    else
                    {
                        m_pos -= part.AbsolutePosition;
                        m_parentPosition = part.AbsolutePosition;
                    }
                }
                else
                {
                    return;
                }
            }
            m_parentID = m_requestedSitTargetID;

            Velocity = Vector3.Zero;
            RemoveFromPhysicalScene();

            Animator.TrySetMovementAnimation(sitAnimation);
            SendFullUpdateToAllClients();
            // This may seem stupid, but Our Full updates don't send avatar rotation :P
            // So we're also sending a terse update (which has avatar rotation)
            // [Update] We do now.
            //SendTerseUpdateToAllClients();
        }

        /// <summary>
        /// Event handler for the 'Always run' setting on the client
        /// Tells the physics plugin to increase speed of movement.
        /// </summary>
        public void HandleSetAlwaysRun(IClientAPI remoteClient, bool pSetAlwaysRun)
        {
            m_setAlwaysRun = pSetAlwaysRun;
            if (PhysicsActor != null)
            {
                PhysicsActor.SetAlwaysRun = pSetAlwaysRun;
            }
        }

        public void HandleStartAnim(IClientAPI remoteClient, UUID animID)
        {
            Animator.AddAnimation(animID, UUID.Zero);
        }

        public void HandleStopAnim(IClientAPI remoteClient, UUID animID)
        {
            Animator.RemoveAnimation(animID);
        }

        /// <summary>
        /// Rotate the avatar to the given rotation and apply a movement in the given relative vector
        /// </summary>
        /// <param name="vec">The vector in which to move.  This is relative to the rotation argument</param>
        /// <param name="rotation">The direction in which this avatar should now face.
        public void AddNewMovement(Vector3 vec, Quaternion rotation)
        {
            if (m_isChildAgent)
            {
                // WHAT???
                m_log.Debug("[SCENEPRESENCE]: AddNewMovement() called on child agent, making root agent!");

                // we have to reset the user's child agent connections.
                // Likely, here they've lost the eventqueue for other regions so border 
                // crossings will fail at this point unless we reset them.

                List<ulong> regions = new List<ulong>(KnownChildRegionHandles);
                regions.Remove(m_scene.RegionInfo.RegionHandle);

                MakeRootAgent(new Vector3(127f, 127f, 127f), true);

                // Async command
                if (m_scene.SceneGridService != null)
                {
                    m_scene.SceneGridService.SendCloseChildAgentConnections(UUID, regions);

                    // Give the above command some time to try and close the connections.
                    // this is really an emergency..   so sleep, or we'll get all discombobulated.
                    System.Threading.Thread.Sleep(500);
                }
                
                if (m_scene.SceneGridService != null)
                {
                    IEntityTransferModule m_agentTransfer = m_scene.RequestModuleInterface<IEntityTransferModule>();
                    if (m_agentTransfer != null)
                        m_agentTransfer.EnableChildAgents(this);
                }
                
                return;
            }

            m_perfMonMS = Util.EnvironmentTickCount();

            Rotation = rotation;
            Vector3 direc = vec * rotation;
            direc.Normalize();

            direc *= 0.03f * 128f * m_speedModifier;

            PhysicsActor actor = m_physicsActor;
            if (actor != null)
            {
                if (actor.Flying)
                {
                    direc *= 4.0f;
                    //bool controlland = (((m_AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) != 0) || ((m_AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG) != 0));
                    //bool colliding = (m_physicsActor.IsColliding==true);
                    //if (controlland)
                    //    m_log.Info("[AGENT]: landCommand");
                    //if (colliding)
                    //    m_log.Info("[AGENT]: colliding");
                    //if (m_physicsActor.Flying && colliding && controlland)
                    //{
                    //    StopFlying();
                    //    m_log.Info("[AGENT]: Stop FLying");
                    //}
                }
                else if (!actor.Flying && actor.IsColliding)
                {
                    if (direc.Z > 2.0f)
                    {
                        direc.Z *= 3.0f;

                        // TODO: PreJump and jump happen too quickly.  Many times prejump gets ignored.
                        Animator.TrySetMovementAnimation("PREJUMP");
                        Animator.TrySetMovementAnimation("JUMP");
                    }
                }
            }

            // TODO: Add the force instead of only setting it to support multiple forces per frame?
            m_forceToApply = direc;

            m_scene.StatsReporter.AddAgentTime(Util.EnvironmentTickCountSubtract(m_perfMonMS));
        }

        #endregion

        #region Overridden Methods

        public override void Update()
        {
            const float ROTATION_TOLERANCE = 0.01f;
            const float VELOCITY_TOLERANCE = 0.001f;
            const float POSITION_TOLERANCE = 0.05f;
            //const int TIME_MS_TOLERANCE = 3000;

            SendPrimUpdates();

            if (m_isChildAgent == false)
            {
//                PhysicsActor actor = m_physicsActor;

                // NOTE: Velocity is not the same as m_velocity. Velocity will attempt to
                // grab the latest PhysicsActor velocity, whereas m_velocity is often
                // storing a requested force instead of an actual traveling velocity

                // Throw away duplicate or insignificant updates
                if (!m_bodyRot.ApproxEquals(m_lastRotation, ROTATION_TOLERANCE) ||
                    !Velocity.ApproxEquals(m_lastVelocity, VELOCITY_TOLERANCE) ||
                    !m_pos.ApproxEquals(m_lastPosition, POSITION_TOLERANCE))
                    //Environment.TickCount - m_lastTerseSent > TIME_MS_TOLERANCE)
                {
                    SendTerseUpdateToAllClients();

                    // Update the "last" values
                    m_lastPosition = m_pos;
                    m_lastRotation = m_bodyRot;
                    m_lastVelocity = Velocity;
                    //m_lastTerseSent = Environment.TickCount;
                }

                // followed suggestion from mic bowman. reversed the two lines below.
                if (m_parentID == 0 && m_physicsActor != null || m_parentID != 0) // Check that we have a physics actor or we're sitting on something
                    CheckForBorderCrossing();
                CheckForSignificantMovement(); // sends update to the modules.
            }
        }

        #endregion

        #region Update Client(s)

        /// <summary>
        /// Sends a location update to the client connected to this scenePresence
        /// </summary>
        /// <param name="remoteClient"></param>
        public void SendTerseUpdateToClient(IClientAPI remoteClient)
        {
            // If the client is inactive, it's getting its updates from another
            // server.
            if (remoteClient.IsActive)
            {
                m_perfMonMS = Util.EnvironmentTickCount();

                PhysicsActor actor = m_physicsActor;
                Vector3 velocity = (actor != null) ? actor.Velocity : Vector3.Zero;

                Vector3 pos = m_pos;
                pos.Z += m_appearance.HipOffset;

                //m_log.DebugFormat("[SCENEPRESENCE]: TerseUpdate: Pos={0} Rot={1} Vel={2}", m_pos, m_bodyRot, m_velocity);

                remoteClient.SendPrimUpdate(this, PrimUpdateFlags.Position | PrimUpdateFlags.Rotation | PrimUpdateFlags.Velocity | PrimUpdateFlags.Acceleration | PrimUpdateFlags.AngularVelocity);

                m_scene.StatsReporter.AddAgentTime(Util.EnvironmentTickCountSubtract(m_perfMonMS));
                m_scene.StatsReporter.AddAgentUpdates(1);
            }
        }

        /// <summary>
        /// Send a location/velocity/accelleration update to all agents in scene
        /// </summary>
        public void SendTerseUpdateToAllClients()
        {
            m_perfMonMS = Util.EnvironmentTickCount();
            
            m_scene.ForEachClient(SendTerseUpdateToClient);

            m_scene.StatsReporter.AddAgentTime(Util.EnvironmentTickCountSubtract(m_perfMonMS));
        }

        public void SendCoarseLocations(List<Vector3> coarseLocations, List<UUID> avatarUUIDs)
        {
            SendCourseLocationsMethod d = m_sendCourseLocationsMethod;
            if (d != null)
            {
                d.Invoke(m_scene.RegionInfo.originRegionID, this, coarseLocations, avatarUUIDs);
            }
        }

        public void SetSendCourseLocationMethod(SendCourseLocationsMethod d)
        {
            if (d != null)
                m_sendCourseLocationsMethod = d;
        }

        public void SendCoarseLocationsDefault(UUID sceneId, ScenePresence p, List<Vector3> coarseLocations, List<UUID> avatarUUIDs)
        {
            m_perfMonMS = Util.EnvironmentTickCount();
            m_controllingClient.SendCoarseLocationUpdate(avatarUUIDs, coarseLocations);
            m_scene.StatsReporter.AddAgentTime(Util.EnvironmentTickCountSubtract(m_perfMonMS));
        }

        /// <summary>
        /// Tell other client about this avatar (The client previously didn't know or had outdated details about this avatar)
        /// </summary>
        /// <param name="remoteAvatar"></param>
        public void SendFullUpdateToOtherClient(ScenePresence remoteAvatar)
        {
            // 2 stage check is needed.
            if (remoteAvatar == null)
                return;
            IClientAPI cl=remoteAvatar.ControllingClient;
            if (cl == null)
                return;
            if (m_appearance.Texture == null)
                return;

            Vector3 pos = m_pos;
            pos.Z += m_appearance.HipOffset;

            remoteAvatar.m_controllingClient.SendAvatarDataImmediate(this);
            m_scene.StatsReporter.AddAgentUpdates(1);
        }

        /// <summary>
        /// Tell *ALL* agents about this agent
        /// </summary>
        public void SendInitialFullUpdateToAllClients()
        {
            m_perfMonMS = Util.EnvironmentTickCount();
            int avUpdates = 0;
            m_scene.ForEachScenePresence(delegate(ScenePresence avatar)
            {
                ++avUpdates;
                // only send if this is the root (children are only "listening posts" in a foreign region)
                if (!IsChildAgent)
                {
                    SendFullUpdateToOtherClient(avatar);
                }

                if (avatar.LocalId != LocalId)
                {
                    if (!avatar.IsChildAgent)
                    {
                        avatar.SendFullUpdateToOtherClient(this);
                        avatar.SendAppearanceToOtherAgent(this);
                        avatar.Animator.SendAnimPackToClient(ControllingClient);
                    }
                }
            });

            m_scene.StatsReporter.AddAgentUpdates(avUpdates);
            m_scene.StatsReporter.AddAgentTime(Util.EnvironmentTickCountSubtract(m_perfMonMS));

            //Animator.SendAnimPack();
        }

        public void SendFullUpdateToAllClients()
        {
            m_perfMonMS = Util.EnvironmentTickCount();

            // only send update from root agents to other clients; children are only "listening posts"
            int count = 0;
            m_scene.ForEachScenePresence(delegate(ScenePresence sp)
            {
                if (sp.IsChildAgent)
                    return;
                SendFullUpdateToOtherClient(sp);
                ++count;
            });
            m_scene.StatsReporter.AddAgentUpdates(count);
            m_scene.StatsReporter.AddAgentTime(Util.EnvironmentTickCountSubtract(m_perfMonMS));

            Animator.SendAnimPack();
        }

        /// <summary>
        /// Do everything required once a client completes its movement into a region
        /// </summary>
        public void SendInitialData()
        {
            // Moved this into CompleteMovement to ensure that m_appearance is initialized before
            // the inventory arrives
            // m_scene.GetAvatarAppearance(m_controllingClient, out m_appearance);

            Vector3 pos = m_pos;
            pos.Z += m_appearance.HipOffset;

            m_controllingClient.SendAvatarDataImmediate(this);

            SendInitialFullUpdateToAllClients();
        }

        /// <summary>
        /// Tell the client for this scene presence what items it should be wearing now
        /// </summary>
        public void SendWearables()
        {
            m_log.DebugFormat("[SCENE]: Received request for wearables of {0}", Name);
            
            ControllingClient.SendWearables(m_appearance.Wearables, m_appearance.Serial++);
        }

        /// <summary>
        ///
        /// </summary>
        public void SendAppearanceToAllOtherAgents()
        {
            m_perfMonMS = Util.EnvironmentTickCount();

            m_scene.ForEachScenePresence(delegate(ScenePresence scenePresence)
                                         {
                                             if (scenePresence.UUID != UUID)
                                             {
                                                 SendAppearanceToOtherAgent(scenePresence);
                                             }
                                         });

            m_scene.StatsReporter.AddAgentTime(Util.EnvironmentTickCountSubtract(m_perfMonMS));
        }

        /// <summary>
        /// Send appearance data to an agent that isn't this one.
        /// </summary>
        /// <param name="avatar"></param>
        public void SendAppearanceToOtherAgent(ScenePresence avatar)
        {
            avatar.ControllingClient.SendAppearance(
                m_appearance.Owner, m_appearance.VisualParams, m_appearance.Texture.GetBytes());
        }

        /// <summary>
        /// Set appearance data (textureentry and slider settings) received from the client
        /// </summary>
        /// <param name="texture"></param>
        /// <param name="visualParam"></param>
        public void SetAppearance(Primitive.TextureEntry textureEntry, byte[] visualParams)
        {
            if (m_physicsActor != null)
            {
                if (!IsChildAgent)
                {
                    // This may seem like it's redundant, remove the avatar from the physics scene
                    // just to add it back again, but it saves us from having to update
                    // 3 variables 10 times a second.
                    bool flyingTemp = m_physicsActor.Flying;
                    RemoveFromPhysicalScene();
                    //m_scene.PhysicsScene.RemoveAvatar(m_physicsActor);

                    //PhysicsActor = null;

                    AddToPhysicalScene(flyingTemp);
                }
            }

            #region Bake Cache Check

            if (textureEntry != null)
            {
                for (int i = 0; i < BAKE_INDICES.Length; i++)
                {
                    int j = BAKE_INDICES[i];
                    Primitive.TextureEntryFace face = textureEntry.FaceTextures[j];

                    if (face != null && face.TextureID != AppearanceManager.DEFAULT_AVATAR_TEXTURE)
                    {
                        if (m_scene.AssetService.Get(face.TextureID.ToString()) == null)
                        {
                            m_log.Warn("[APPEARANCE]: Missing baked texture " + face.TextureID + " (" + j + ") for avatar " + this.Name);
                            this.ControllingClient.SendRebakeAvatarTextures(face.TextureID);
                        }
                    }
                }

            }


            #endregion Bake Cache Check

            m_appearance.SetAppearance(textureEntry, visualParams);
            if (m_appearance.AvatarHeight > 0)
                SetHeight(m_appearance.AvatarHeight);

            // This is not needed, because only the transient data changed
            //AvatarData adata = new AvatarData(m_appearance);
            //m_scene.AvatarService.SetAvatar(m_controllingClient.AgentId, adata);

            SendAppearanceToAllOtherAgents();
            if (!m_startAnimationSet)
            {
                Animator.UpdateMovementAnimations();
                m_startAnimationSet = true;
            }

            Vector3 pos = m_pos;
            pos.Z += m_appearance.HipOffset;

            m_controllingClient.SendAvatarDataImmediate(this);
        }

        public void SetWearable(int wearableId, AvatarWearable wearable)
        {
            m_appearance.SetWearable(wearableId, wearable);
            AvatarData adata = new AvatarData(m_appearance);
            m_scene.AvatarService.SetAvatar(m_controllingClient.AgentId, adata);
            m_controllingClient.SendWearables(m_appearance.Wearables, m_appearance.Serial++);
        }

        // Because appearance setting is in a module, we actually need
        // to give it access to our appearance directly, otherwise we
        // get a synchronization issue.
        public AvatarAppearance Appearance
        {
            get { return m_appearance; }
            set { m_appearance = value; }
        }

        #endregion

        #region Significant Movement Method

        /// <summary>
        /// This checks for a significant movement and sends a courselocationchange update
        /// </summary>
        protected void CheckForSignificantMovement()
        {
            // Movement updates for agents in neighboring regions are sent directly to clients.
            // This value only affects how often agent positions are sent to neighbor regions
            // for things such as distance-based update prioritization
            const float SIGNIFICANT_MOVEMENT = 2.0f;

            if (Util.GetDistanceTo(AbsolutePosition, posLastSignificantMove) > SIGNIFICANT_MOVEMENT)
            {
                posLastSignificantMove = AbsolutePosition;
                m_scene.EventManager.TriggerSignificantClientMovement(m_controllingClient);
            }

            // Minimum Draw distance is 64 meters, the Radius of the draw distance sphere is 32m
            if (Util.GetDistanceTo(AbsolutePosition, m_lastChildAgentUpdatePosition) >= Scene.ChildReprioritizationDistance ||
                Util.GetDistanceTo(CameraPosition, m_lastChildAgentUpdateCamPosition) >= Scene.ChildReprioritizationDistance)
            {
                m_lastChildAgentUpdatePosition = AbsolutePosition;
                m_lastChildAgentUpdateCamPosition = CameraPosition;

                ChildAgentDataUpdate cadu = new ChildAgentDataUpdate();
                cadu.ActiveGroupID = UUID.Zero.Guid;
                cadu.AgentID = UUID.Guid;
                cadu.alwaysrun = m_setAlwaysRun;
                cadu.AVHeight = m_avHeight;
                Vector3 tempCameraCenter = m_CameraCenter;
                cadu.cameraPosition = tempCameraCenter;
                cadu.drawdistance = m_DrawDistance;
                cadu.GroupAccess = 0;
                cadu.Position = AbsolutePosition;
                cadu.regionHandle = m_rootRegionHandle;
                float multiplier = 1;
                int innacurateNeighbors = m_scene.GetInaccurateNeighborCount();
                if (innacurateNeighbors != 0)
                {
                    multiplier = 1f / (float)innacurateNeighbors;
                }
                if (multiplier <= 0f)
                {
                    multiplier = 0.25f;
                }

                //m_log.Info("[NeighborThrottle]: " + m_scene.GetInaccurateNeighborCount().ToString() + " - m: " + multiplier.ToString());
                cadu.throttles = ControllingClient.GetThrottlesPacked(multiplier);
                cadu.Velocity = Velocity;

                AgentPosition agentpos = new AgentPosition();
                agentpos.CopyFrom(cadu);

                m_scene.SendOutChildAgentUpdates(agentpos, this);
            }
        }

        #endregion

        #region Border Crossing Methods

        /// <summary>
        /// Checks to see if the avatar is in range of a border and calls CrossToNewRegion
        /// </summary>
        protected void CheckForBorderCrossing()
        {
            if (IsChildAgent)
                return;

            Vector3 pos2 = AbsolutePosition;
            Vector3 vel = Velocity;
            int neighbor = 0;
            int[] fix = new int[2];

            float timeStep = 0.1f;
            pos2.X = pos2.X + (vel.X*timeStep);
            pos2.Y = pos2.Y + (vel.Y*timeStep);
            pos2.Z = pos2.Z + (vel.Z*timeStep);

            if (!IsInTransit)
            {
                // Checks if where it's headed exists a region

                bool needsTransit = false;
                if (m_scene.TestBorderCross(pos2, Cardinals.W))
                {
                    if (m_scene.TestBorderCross(pos2, Cardinals.S))
                    {
                        needsTransit = true;
                        neighbor = HaveNeighbor(Cardinals.SW, ref fix);
                    }
                    else if (m_scene.TestBorderCross(pos2, Cardinals.N))
                    {
                        needsTransit = true;
                        neighbor = HaveNeighbor(Cardinals.NW, ref fix);
                    }
                    else
                    {
                        needsTransit = true;
                        neighbor = HaveNeighbor(Cardinals.W, ref fix);
                    }
                }
                else if (m_scene.TestBorderCross(pos2, Cardinals.E))
                {
                    if (m_scene.TestBorderCross(pos2, Cardinals.S))
                    {
                        needsTransit = true;
                        neighbor = HaveNeighbor(Cardinals.SE, ref fix);
                    }
                    else if (m_scene.TestBorderCross(pos2, Cardinals.N))
                    {
                        needsTransit = true;
                        neighbor = HaveNeighbor(Cardinals.NE, ref fix);
                    }
                    else
                    {
                        needsTransit = true;
                        neighbor = HaveNeighbor(Cardinals.E, ref fix);
                    }
                }
                else if (m_scene.TestBorderCross(pos2, Cardinals.S))
                {
                    needsTransit = true;
                    neighbor = HaveNeighbor(Cardinals.S, ref fix);
                }
                else if (m_scene.TestBorderCross(pos2, Cardinals.N))
                {
                    needsTransit = true;
                    neighbor = HaveNeighbor(Cardinals.N, ref fix);
                }


                // Makes sure avatar does not end up outside region
                if (neighbor <= 0)
                {
                    if (!needsTransit)
                    {
                        if (m_requestedSitTargetUUID == UUID.Zero)
                        {
                            Vector3 pos = AbsolutePosition;
                            if (AbsolutePosition.X < 0)
                                pos.X += Velocity.X;
                            else if (AbsolutePosition.X > Constants.RegionSize)
                                pos.X -= Velocity.X;
                            if (AbsolutePosition.Y < 0)
                                pos.Y += Velocity.Y;
                            else if (AbsolutePosition.Y > Constants.RegionSize)
                                pos.Y -= Velocity.Y;
                            AbsolutePosition = pos;
                        }
                    }
                }
                else if (neighbor > 0)
                    CrossToNewRegion();
            }
            else
            {
                RemoveFromPhysicalScene();
                // This constant has been inferred from experimentation
                // I'm not sure what this value should be, so I tried a few values.
                timeStep = 0.04f;
                pos2 = AbsolutePosition;
                pos2.X = pos2.X + (vel.X * timeStep);
                pos2.Y = pos2.Y + (vel.Y * timeStep);
                pos2.Z = pos2.Z + (vel.Z * timeStep);
                m_pos = pos2;
            }
        }

        protected int HaveNeighbor(Cardinals car, ref int[] fix)
        {
            uint neighbourx = m_regionInfo.RegionLocX;
            uint neighboury = m_regionInfo.RegionLocY;

            int dir = (int)car;

            if (dir > 1 && dir < 5) //Heading East
                neighbourx++;
            else if (dir > 5) // Heading West
                neighbourx--;

            if (dir < 3 || dir == 8) // Heading North
                neighboury++;
            else if (dir > 3 && dir < 7) // Heading Sout
                neighboury--;

            int x = (int)(neighbourx * Constants.RegionSize);
            int y = (int)(neighboury * Constants.RegionSize);
            GridRegion neighbourRegion = m_scene.GridService.GetRegionByPosition(m_scene.RegionInfo.ScopeID, x, y);

            if (neighbourRegion == null)
            {
                fix[0] = (int)(m_regionInfo.RegionLocX - neighbourx);
                fix[1] = (int)(m_regionInfo.RegionLocY - neighboury);
                return dir * (-1);
            }
            else
                return dir;
        }

        /// <summary>
        /// Moves the agent outside the region bounds
        /// Tells neighbor region that we're crossing to it
        /// If the neighbor accepts, remove the agent's viewable avatar from this scene
        /// set them to a child agent.
        /// </summary>
        protected void CrossToNewRegion()
        {
            InTransit();
            try
            {
                m_scene.CrossAgentToNewRegion(this, m_physicsActor.Flying);
            }
            catch
            {
                m_scene.CrossAgentToNewRegion(this, false);
            }
        }

        public void InTransit()
        {
            m_inTransit = true;

            if ((m_physicsActor != null) && m_physicsActor.Flying)
                m_AgentControlFlags |= AgentManager.ControlFlags.AGENT_CONTROL_FLY;
            else if ((m_AgentControlFlags & AgentManager.ControlFlags.AGENT_CONTROL_FLY) != 0)
                m_AgentControlFlags &= ~AgentManager.ControlFlags.AGENT_CONTROL_FLY;
        }

        public void NotInTransit()
        {
            m_inTransit = false;
        }

        public void RestoreInCurrentScene()
        {
            AddToPhysicalScene(false); // not exactly false
        }

        public void Reset()
        {
            // Put the child agent back at the center
            AbsolutePosition 
                = new Vector3(((float)Constants.RegionSize * 0.5f), ((float)Constants.RegionSize * 0.5f), 70);
            Animator.ResetAnimations();
        }

        /// <summary>
        /// Computes which child agents to close when the scene presence moves to another region.
        /// Removes those regions from m_knownRegions.
        /// </summary>
        /// <param name="newRegionX">The new region's x on the map</param>
        /// <param name="newRegionY">The new region's y on the map</param>
        /// <returns></returns>
        public void CloseChildAgents(uint newRegionX, uint newRegionY)
        {
            List<ulong> byebyeRegions = new List<ulong>();
            m_log.DebugFormat(
                "[SCENE PRESENCE]: Closing child agents. Checking {0} regions in {1}", 
                m_knownChildRegions.Keys.Count, Scene.RegionInfo.RegionName);
            //DumpKnownRegions();

            lock (m_knownChildRegions)
            {
                foreach (ulong handle in m_knownChildRegions.Keys)
                {
                    // Don't close the agent on this region yet
                    if (handle != Scene.RegionInfo.RegionHandle)
                    {
                        uint x, y;
                        Utils.LongToUInts(handle, out x, out y);
                        x = x / Constants.RegionSize;
                        y = y / Constants.RegionSize;

                        //m_log.Debug("---> x: " + x + "; newx:" + newRegionX + "; Abs:" + (int)Math.Abs((int)(x - newRegionX)));
                        //m_log.Debug("---> y: " + y + "; newy:" + newRegionY + "; Abs:" + (int)Math.Abs((int)(y - newRegionY)));
                        if (Util.IsOutsideView(x, newRegionX, y, newRegionY))
                        {
                            byebyeRegions.Add(handle);
                        }
                    }
                }
            }
            
            if (byebyeRegions.Count > 0)
            {
                m_log.Debug("[SCENE PRESENCE]: Closing " + byebyeRegions.Count + " child agents");
                m_scene.SceneGridService.SendCloseChildAgentConnections(m_controllingClient.AgentId, byebyeRegions);
            }
            
            foreach (ulong handle in byebyeRegions)
            {
                RemoveNeighbourRegion(handle);
            }
        }

        #endregion

        /// <summary>
        /// This allows the Sim owner the abiility to kick users from their sim currently.
        /// It tells the client that the agent has permission to do so.
        /// </summary>
        public void GrantGodlikePowers(UUID agentID, UUID sessionID, UUID token, bool godStatus)
        {
            if (godStatus)
            {
                // For now, assign god level 200 to anyone
                // who is granted god powers, but has no god level set.
                //
                UserAccount account = m_scene.UserAccountService.GetUserAccount(m_scene.RegionInfo.ScopeID, agentID);
                if (account != null)
                {
                    if (account.UserLevel > 0)
                        m_godLevel = account.UserLevel;
                    else
                        m_godLevel = 200;
                }
            }
            else
            {
                m_godLevel = 0;
            }

            ControllingClient.SendAdminResponse(token, (uint)m_godLevel);
        }

        #region Child Agent Updates

        public void ChildAgentDataUpdate(AgentData cAgentData)
        {
            //m_log.Debug("   >>> ChildAgentDataUpdate <<< " + Scene.RegionInfo.RegionName);
            if (!IsChildAgent)
                return;

            CopyFrom(cAgentData);
        }

        /// <summary>
        /// This updates important decision making data about a child agent
        /// The main purpose is to figure out what objects to send to a child agent that's in a neighboring region
        /// </summary>
        public void ChildAgentDataUpdate(AgentPosition cAgentData, uint tRegionX, uint tRegionY, uint rRegionX, uint rRegionY)
        {
            if (!IsChildAgent)
                return;

            //m_log.Debug("   >>> ChildAgentPositionUpdate <<< " + rRegionX + "-" + rRegionY);
            int shiftx = ((int)rRegionX - (int)tRegionX) * (int)Constants.RegionSize;
            int shifty = ((int)rRegionY - (int)tRegionY) * (int)Constants.RegionSize;

            Vector3 offset = new Vector3(shiftx, shifty, 0f);

            m_DrawDistance = cAgentData.Far;
            if (cAgentData.Position != new Vector3(-1f, -1f, -1f)) // UGH!!
                m_pos = cAgentData.Position + offset;

            if (Vector3.Distance(AbsolutePosition, posLastSignificantMove) >= Scene.ChildReprioritizationDistance)
            {
                posLastSignificantMove = AbsolutePosition;
                ReprioritizeUpdates();
            }

            m_CameraCenter = cAgentData.Center + offset;

            m_avHeight = cAgentData.Size.Z;
            //SetHeight(cAgentData.AVHeight);

            if ((cAgentData.Throttles != null) && cAgentData.Throttles.Length > 0)
                ControllingClient.SetChildAgentThrottle(cAgentData.Throttles);

            // Sends out the objects in the user's draw distance if m_sendTasksToChild is true.
            if (m_scene.m_seeIntoRegionFromNeighbor)
                m_sceneViewer.Reset();

            //cAgentData.AVHeight;
            m_rootRegionHandle = cAgentData.RegionHandle;
            //m_velocity = cAgentData.Velocity;
        }

        public void CopyTo(AgentData cAgent)
        {
            cAgent.AgentID = UUID;
            cAgent.RegionID = Scene.RegionInfo.RegionID;

            cAgent.Position = AbsolutePosition;
            cAgent.Velocity = m_velocity;
            cAgent.Center = m_CameraCenter;
            // Don't copy the size; it is inferred from apearance parameters
            //cAgent.Size = new Vector3(0, 0, m_avHeight);
            cAgent.AtAxis = m_CameraAtAxis;
            cAgent.LeftAxis = m_CameraLeftAxis;
            cAgent.UpAxis = m_CameraUpAxis;

            cAgent.Far = m_DrawDistance;

            // Throttles 
            float multiplier = 1;
            int innacurateNeighbors = m_scene.GetInaccurateNeighborCount();
            if (innacurateNeighbors != 0)
            {
                multiplier = 1f / innacurateNeighbors;
            }
            if (multiplier <= 0f)
            {
                multiplier = 0.25f;
            }
            //m_log.Info("[NeighborThrottle]: " + m_scene.GetInaccurateNeighborCount().ToString() + " - m: " + multiplier.ToString());
            cAgent.Throttles = ControllingClient.GetThrottlesPacked(multiplier);

            cAgent.HeadRotation = m_headrotation;
            cAgent.BodyRotation = m_bodyRot;
            cAgent.ControlFlags = (uint)m_AgentControlFlags;

            if (m_scene.Permissions.IsGod(new UUID(cAgent.AgentID)))
                cAgent.GodLevel = (byte)m_godLevel;
            else 
                cAgent.GodLevel = (byte) 0;

            cAgent.AlwaysRun = m_setAlwaysRun;

            try
            {
                // We might not pass the Wearables in all cases...
                // They're only needed so that persistent changes to the appearance
                // are preserved in the new region where the user is moving to.
                // But in Hypergrid we might not let this happen.
                int i = 0;
                UUID[] wears = new UUID[m_appearance.Wearables.Length * 2];
                foreach (AvatarWearable aw in m_appearance.Wearables)
                {
                    if (aw != null)
                    {
                        wears[i++] = aw.ItemID;
                        wears[i++] = aw.AssetID;
                    }
                    else
                    {
                        wears[i++] = UUID.Zero;
                        wears[i++] = UUID.Zero;
                    }
                }
                cAgent.Wearables = wears;

                cAgent.VisualParams = m_appearance.VisualParams;

                if (m_appearance.Texture != null)
                    cAgent.AgentTextures = m_appearance.Texture.GetBytes();
            }
            catch (Exception e)
            {
                m_log.Warn("[SCENE PRESENCE]: exception in CopyTo " + e.Message);
            }

            //Attachments
            List<int> attPoints = m_appearance.GetAttachedPoints();
            if (attPoints != null)
            {
                //m_log.DebugFormat("[SCENE PRESENCE]: attachments {0}", attPoints.Count);
                int i = 0;
                AttachmentData[] attachs = new AttachmentData[attPoints.Count];
                foreach (int point in attPoints)
                {
                    attachs[i++] = new AttachmentData(point, m_appearance.GetAttachedItem(point), m_appearance.GetAttachedAsset(point));
                }
                cAgent.Attachments = attachs;
            }

            // Animations
            try
            {
                cAgent.Anims = Animator.Animations.ToArray();
            }
            catch { }

            // cAgent.GroupID = ??
            // Groups???

        }

        public void CopyFrom(AgentData cAgent)
        {
            m_originRegionID = cAgent.RegionID;

            m_callbackURI = cAgent.CallbackURI;

            m_pos = cAgent.Position;
            m_velocity = cAgent.Velocity;
            m_CameraCenter = cAgent.Center;
            //m_avHeight = cAgent.Size.Z;
            m_CameraAtAxis = cAgent.AtAxis;
            m_CameraLeftAxis = cAgent.LeftAxis;
            m_CameraUpAxis = cAgent.UpAxis;

            m_DrawDistance = cAgent.Far;

            if ((cAgent.Throttles != null) && cAgent.Throttles.Length > 0)
                ControllingClient.SetChildAgentThrottle(cAgent.Throttles);

            m_headrotation = cAgent.HeadRotation;
            m_bodyRot = cAgent.BodyRotation;
            m_AgentControlFlags = (AgentManager.ControlFlags)cAgent.ControlFlags; 

            if (m_scene.Permissions.IsGod(new UUID(cAgent.AgentID)))
                m_godLevel = cAgent.GodLevel;
            m_setAlwaysRun = cAgent.AlwaysRun;

            uint i = 0;
            try
            {
                if (cAgent.Wearables == null)
                   cAgent.Wearables  = new UUID[0];
                AvatarWearable[] wears = new AvatarWearable[cAgent.Wearables.Length / 2];
                for (uint n = 0; n < cAgent.Wearables.Length; n += 2)
                {
                    UUID itemId = cAgent.Wearables[n];
                    UUID assetId = cAgent.Wearables[n + 1];
                    wears[i++] = new AvatarWearable(itemId, assetId);
                }
                m_appearance.Wearables = wears;
                Primitive.TextureEntry te;
                if (cAgent.AgentTextures != null && cAgent.AgentTextures.Length > 1)
                    te = new Primitive.TextureEntry(cAgent.AgentTextures, 0, cAgent.AgentTextures.Length);
                else
                    te = AvatarAppearance.GetDefaultTexture();
                if ((cAgent.VisualParams == null) || (cAgent.VisualParams.Length < AvatarAppearance.VISUALPARAM_COUNT))
                    cAgent.VisualParams = AvatarAppearance.GetDefaultVisualParams();
                m_appearance.SetAppearance(te, (byte[])cAgent.VisualParams.Clone());
            }
            catch (Exception e)
            {
                m_log.Warn("[SCENE PRESENCE]: exception in CopyFrom " + e.Message);
            }

            // Attachments
            try
            {
                if (cAgent.Attachments != null)
                {
                    m_appearance.ClearAttachments();
                    foreach (AttachmentData att in cAgent.Attachments)
                    {
                        m_appearance.SetAttachment(att.AttachPoint, att.ItemID, att.AssetID);
                    }
                }
            }
            catch { } 

            // Animations
            try
            {
                Animator.ResetAnimations();
                Animator.Animations.FromArray(cAgent.Anims);
            }
            catch {  }

            //cAgent.GroupID = ??
            //Groups???
        }

        public bool CopyAgent(out IAgentData agent)
        {
            agent = new CompleteAgentData();
            CopyTo((AgentData)agent);
            return true;
        }

        #endregion Child Agent Updates

        /// <summary>
        /// Handles part of the PID controller function for moving an avatar.
        /// </summary>
        public override void UpdateMovement()
        {
            if (m_forceToApply.HasValue)
            {
                Vector3 force = m_forceToApply.Value;

                m_updateflag = true;
//                movementvector = force;
                Velocity = force;

                m_forceToApply = null;
            }
        }

        public override void SetText(string text, Vector3 color, double alpha)
        {
            throw new Exception("Can't set Text on avatar.");
        }

        /// <summary>
        /// Adds a physical representation of the avatar to the Physics plugin
        /// </summary>
        public void AddToPhysicalScene(bool isFlying)
        {
            PhysicsScene scene = m_scene.PhysicsScene;

            Vector3 pVec = AbsolutePosition;

            // Old bug where the height was in centimeters instead of meters
            if (m_avHeight == 127.0f)
            {
                m_physicsActor = scene.AddAvatar(Firstname + "." + Lastname, pVec, new Vector3(0f, 0f, 1.56f),
                                                 isFlying);
            }
            else
            {
                m_physicsActor = scene.AddAvatar(Firstname + "." + Lastname, pVec,
                                                 new Vector3(0f, 0f, m_avHeight), isFlying);
            }
            scene.AddPhysicsActorTaint(m_physicsActor);
            //m_physicsActor.OnRequestTerseUpdate += SendTerseUpdateToAllClients;
            m_physicsActor.OnCollisionUpdate += PhysicsCollisionUpdate;
            m_physicsActor.OnOutOfBounds += OutOfBoundsCall; // Called for PhysicsActors when there's something wrong
            m_physicsActor.SubscribeEvents(500);
            m_physicsActor.LocalID = LocalId;
        }

        private void OutOfBoundsCall(Vector3 pos)
        {
            //bool flying = m_physicsActor.Flying;
            //RemoveFromPhysicalScene();

            //AddToPhysicalScene(flying);
            if (ControllingClient != null)
                ControllingClient.SendAgentAlertMessage("Physics is having a problem with your avatar.  You may not be able to move until you relog.", true);
        }

        // Event called by the physics plugin to tell the avatar about a collision.
        private void PhysicsCollisionUpdate(EventArgs e)
        {
            if (e == null)
                return;

            //if ((Math.Abs(Velocity.X) > 0.1e-9f) || (Math.Abs(Velocity.Y) > 0.1e-9f))
            // The Physics Scene will send updates every 500 ms grep: m_physicsActor.SubscribeEvents(
            // as of this comment the interval is set in AddToPhysicalScene
            if (Animator!=null)
                Animator.UpdateMovementAnimations();

            CollisionEventUpdate collisionData = (CollisionEventUpdate)e;
            Dictionary<uint, ContactPoint> coldata = collisionData.m_objCollisionList;

            CollisionPlane = Vector4.UnitW;

            if (coldata.Count != 0 && Animator != null)
            {
                switch (Animator.CurrentMovementAnimation)
                {
                    case "STAND":
                    case "WALK":
                    case "RUN":
                    case "CROUCH":
                    case "CROUCHWALK":
                        {
                            ContactPoint lowest;
                            lowest.SurfaceNormal = Vector3.Zero;
                            lowest.Position = Vector3.Zero;
                            lowest.Position.Z = Single.NaN;

                            foreach (ContactPoint contact in coldata.Values)
                            {
                                if (Single.IsNaN(lowest.Position.Z) || contact.Position.Z < lowest.Position.Z)
                                {
                                    lowest = contact;
                                }
                            }

                            CollisionPlane = new Vector4(-lowest.SurfaceNormal, -Vector3.Dot(lowest.Position, lowest.SurfaceNormal));
                        }
                        break;
                }
            }

            if (m_invulnerable)
                return;
            
            float starthealth = Health;
            uint killerObj = 0;
            foreach (uint localid in coldata.Keys)
            {
                SceneObjectPart part = Scene.GetSceneObjectPart(localid);

                if (part != null && part.ParentGroup.Damage != -1.0f)
                    Health -= part.ParentGroup.Damage;
                else
                {
                    if (coldata[localid].PenetrationDepth >= 0.10f)
                        Health -= coldata[localid].PenetrationDepth * 5.0f;
                }

                if (Health <= 0.0f)
                {
                    if (localid != 0)
                        killerObj = localid;
                }
                //m_log.Debug("[AVATAR]: Collision with localid: " + localid.ToString() + " at depth: " + coldata[localid].ToString());
            }
            //Health = 100;
            if (!m_invulnerable)
            {
                if (starthealth != Health)
                {
                    ControllingClient.SendHealth(Health);
                }
                if (m_health <= 0)
                    m_scene.EventManager.TriggerAvatarKill(killerObj, this);
            }
        }

        public void setHealthWithUpdate(float health)
        {
            Health = health;
            ControllingClient.SendHealth(Health);
        }

        public void Close()
        {
            lock (m_attachments)
            {
                // Delete attachments from scene
                // Don't try to save, as this thread won't live long
                // enough to complete the save. This would cause no copy
                // attachments to poof!
                //
                foreach (SceneObjectGroup grp in m_attachments)
                {
                    m_scene.DeleteSceneObject(grp, false);
                }
                m_attachments.Clear();
            }
            
            lock (m_knownChildRegions)
            {
                m_knownChildRegions.Clear();
            }

            lock (m_reprioritization_timer)
            {
                m_reprioritization_timer.Enabled = false;
                m_reprioritization_timer.Elapsed -= new ElapsedEventHandler(Reprioritize);
            }
            
            // I don't get it but mono crashes when you try to dispose of this timer,
            // unsetting the elapsed callback should be enough to allow for cleanup however.
            // m_reprioritizationTimer.Dispose(); 

            m_sceneViewer.Close();

            RemoveFromPhysicalScene();
            m_animator.Close();
            m_animator = null;
        }

        public void AddAttachment(SceneObjectGroup gobj)
        {
            lock (m_attachments)
            {
                m_attachments.Add(gobj);
            }
        }

        public bool HasAttachments()
        {
            return m_attachments.Count > 0;
        }

        public bool HasScriptedAttachments()
        {
            lock (m_attachments)
            {
                foreach (SceneObjectGroup gobj in m_attachments)
                {
                    if (gobj != null)
                    {
                        if (gobj.RootPart.Inventory.ContainsScripts())
                            return true;
                    }
                }
            }
            return false;
        }

        public void RemoveAttachment(SceneObjectGroup gobj)
        {
            lock (m_attachments)
            {
                if (m_attachments.Contains(gobj))
                {
                    m_attachments.Remove(gobj);
                }
            }
        }

        public bool ValidateAttachments()
        {
            lock (m_attachments)
            {
                // Validate
                foreach (SceneObjectGroup gobj in m_attachments)
                {
                    if (gobj == null)
                        return false;

                    if (gobj.IsDeleted)
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Send a script event to this scene presence's attachments
        /// </summary>
        /// <param name="eventName">The name of the event</param>
        /// <param name="args">The arguments for the event</param>
        public void SendScriptEventToAttachments(string eventName, Object[] args)
        {
            if (m_scriptEngines != null)
            {
                lock (m_attachments)
                {
                    foreach (SceneObjectGroup grp in m_attachments)
                    {
                        // 16384 is CHANGED_ANIMATION
                        //
                        // Send this to all attachment root prims
                        //
                        foreach (IScriptModule m in m_scriptEngines)
                        {
                            if (m == null) // No script engine loaded
                                continue;

                            m.PostObjectEvent(grp.RootPart.UUID, "changed", new Object[] { 16384 });
                        }
                    }
                }
            }
        }


        public void initializeScenePresence(IClientAPI client, RegionInfo region, Scene scene)
        {
            m_controllingClient = client;
            m_regionInfo = region;
            m_scene = scene;

            RegisterToEvents();

            /*
            AbsolutePosition = client.StartPos;

            Animations = new AvatarAnimations();
            Animations.LoadAnims();

            m_animations = new List<UUID>();
            m_animations.Add(Animations.AnimsUUID["STAND"]);
            m_animationSeqs.Add(m_controllingClient.NextAnimationSequenceNumber);

            SetDirectionVectors();
            */
        }

        internal void PushForce(Vector3 impulse)
        {
            if (PhysicsActor != null)
            {
                PhysicsActor.AddForce(impulse,true);
            }
        }

        public void RegisterControlEventsToScript(int controls, int accept, int pass_on, uint Obj_localID, UUID Script_item_UUID)
        {
            ScriptControllers obj = new ScriptControllers();
            obj.ignoreControls = ScriptControlled.CONTROL_ZERO;
            obj.eventControls = ScriptControlled.CONTROL_ZERO;

            obj.itemID = Script_item_UUID;
            obj.objID = Obj_localID;
            if (pass_on == 0 && accept == 0)
            {
                IgnoredControls |= (ScriptControlled)controls;
                obj.ignoreControls = (ScriptControlled)controls;
            }

            if (pass_on == 0 && accept == 1)
            {
                IgnoredControls |= (ScriptControlled)controls;
                obj.ignoreControls = (ScriptControlled)controls;
                obj.eventControls = (ScriptControlled)controls;
            }
            if (pass_on == 1 && accept == 1)
            {
                IgnoredControls = ScriptControlled.CONTROL_ZERO;
                obj.eventControls = (ScriptControlled)controls;
                obj.ignoreControls = ScriptControlled.CONTROL_ZERO;
            }

            lock (scriptedcontrols)
            {
                if (pass_on == 1 && accept == 0)
                {
                    IgnoredControls &= ~(ScriptControlled)controls;
                    if (scriptedcontrols.ContainsKey(Script_item_UUID))
                        scriptedcontrols.Remove(Script_item_UUID);
                }
                else
                {
                    scriptedcontrols[Script_item_UUID] = obj;
                }
            }
            ControllingClient.SendTakeControls(controls, pass_on == 1 ? true : false, true);
        }

        public void HandleForceReleaseControls(IClientAPI remoteClient, UUID agentID)
        {
            IgnoredControls = ScriptControlled.CONTROL_ZERO;
            lock (scriptedcontrols)
            {
                scriptedcontrols.Clear();
            }
            ControllingClient.SendTakeControls(int.MaxValue, false, false);
        }

        public void UnRegisterControlEventsToScript(uint Obj_localID, UUID Script_item_UUID)
        {
            ScriptControllers takecontrols;

            lock (scriptedcontrols)
            {
                if (scriptedcontrols.TryGetValue(Script_item_UUID, out takecontrols))
                {
                    ScriptControlled sctc = takecontrols.eventControls;

                    ControllingClient.SendTakeControls((int)sctc, false, false);
                    ControllingClient.SendTakeControls((int)sctc, true, false);

                    scriptedcontrols.Remove(Script_item_UUID);
                    IgnoredControls = ScriptControlled.CONTROL_ZERO;
                    foreach (ScriptControllers scData in scriptedcontrols.Values)
                    {
                        IgnoredControls |= scData.ignoreControls;
                    }
                }
            }
        }

        internal void SendControlToScripts(uint flags)
        {
            ScriptControlled allflags = ScriptControlled.CONTROL_ZERO;

            if (MouseDown)
            {
                allflags = LastCommands & (ScriptControlled.CONTROL_ML_LBUTTON | ScriptControlled.CONTROL_LBUTTON);
                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_UP) != 0 || (flags & unchecked((uint)AgentManager.ControlFlags.AGENT_CONTROL_ML_LBUTTON_UP)) != 0)
                {
                    allflags = ScriptControlled.CONTROL_ZERO;
                    MouseDown = true;
                }
            }

            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_ML_LBUTTON_DOWN) != 0)
            {
                allflags |= ScriptControlled.CONTROL_ML_LBUTTON;
                MouseDown = true;
            }
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_DOWN) != 0)
            {
                allflags |= ScriptControlled.CONTROL_LBUTTON;
                MouseDown = true;
            }

            // find all activated controls, whether the scripts are interested in them or not
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_POS) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_POS) != 0)
            {
                allflags |= ScriptControlled.CONTROL_FWD;
            }
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_NEG) != 0)
            {
                allflags |= ScriptControlled.CONTROL_BACK;
            }
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_POS) != 0)
            {
                allflags |= ScriptControlled.CONTROL_UP;
            }
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG) != 0)
            {
                allflags |= ScriptControlled.CONTROL_DOWN;
            }
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_POS) != 0)
            {
                allflags |= ScriptControlled.CONTROL_LEFT;
            }
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_NEG) != 0)
            {
                allflags |= ScriptControlled.CONTROL_RIGHT;
            }
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_YAW_NEG) != 0)
            {
                allflags |= ScriptControlled.CONTROL_ROT_RIGHT;
            }
            if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_YAW_POS) != 0)
            {
                allflags |= ScriptControlled.CONTROL_ROT_LEFT;
            }
            // optimization; we have to check per script, but if nothing is pressed and nothing changed, we can skip that
            if (allflags != ScriptControlled.CONTROL_ZERO || allflags != LastCommands)
            {
                lock (scriptedcontrols)
                {
                    foreach (KeyValuePair<UUID, ScriptControllers> kvp in scriptedcontrols)
                    {
                        UUID scriptUUID = kvp.Key;
                        ScriptControllers scriptControlData = kvp.Value;

                        ScriptControlled localHeld = allflags & scriptControlData.eventControls;     // the flags interesting for us
                        ScriptControlled localLast = LastCommands & scriptControlData.eventControls; // the activated controls in the last cycle
                        ScriptControlled localChange = localHeld ^ localLast;                        // the changed bits
                        if (localHeld != ScriptControlled.CONTROL_ZERO || localChange != ScriptControlled.CONTROL_ZERO)
                        {
                            // only send if still pressed or just changed
                            m_scene.EventManager.TriggerControlEvent(scriptControlData.objID, scriptUUID, UUID, (uint)localHeld, (uint)localChange);
                        }
                    }
                }
            }

            LastCommands = allflags;
        }

        internal static AgentManager.ControlFlags RemoveIgnoredControls(AgentManager.ControlFlags flags, ScriptControlled ignored)
        {
            if (ignored == ScriptControlled.CONTROL_ZERO)
                return flags;

            if ((ignored & ScriptControlled.CONTROL_BACK) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG | AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_NEG);
            if ((ignored & ScriptControlled.CONTROL_FWD) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_POS | AgentManager.ControlFlags.AGENT_CONTROL_AT_POS);
            if ((ignored & ScriptControlled.CONTROL_DOWN) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG | AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG);
            if ((ignored & ScriptControlled.CONTROL_UP) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_POS | AgentManager.ControlFlags.AGENT_CONTROL_UP_POS);
            if ((ignored & ScriptControlled.CONTROL_LEFT) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS | AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_POS);
            if ((ignored & ScriptControlled.CONTROL_RIGHT) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_NEG | AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG);
            if ((ignored & ScriptControlled.CONTROL_ROT_LEFT) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_YAW_NEG);
            if ((ignored & ScriptControlled.CONTROL_ROT_RIGHT) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_YAW_POS);
            if ((ignored & ScriptControlled.CONTROL_ML_LBUTTON) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_ML_LBUTTON_DOWN);
            if ((ignored & ScriptControlled.CONTROL_LBUTTON) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_UP | AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_DOWN);

            //DIR_CONTROL_FLAG_FORWARD = AgentManager.ControlFlags.AGENT_CONTROL_AT_POS,
            //DIR_CONTROL_FLAG_BACK = AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG,
            //DIR_CONTROL_FLAG_LEFT = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS,
            //DIR_CONTROL_FLAG_RIGHT = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG,
            //DIR_CONTROL_FLAG_UP = AgentManager.ControlFlags.AGENT_CONTROL_UP_POS,
            //DIR_CONTROL_FLAG_DOWN = AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG,
            //DIR_CONTROL_FLAG_DOWN_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG

            return flags;
        }

        /// <summary>
        /// RezAttachments. This should only be called upon login on the first region.
        /// Attachment rezzings on crossings and TPs are done in a different way.
        /// </summary>
        public void RezAttachments()
        {
            if (null == m_appearance)
            {
                m_log.WarnFormat("[ATTACHMENT]: Appearance has not been initialized for agent {0}", UUID);
                return;
            }

            List<int> attPoints = m_appearance.GetAttachedPoints();
            foreach (int p in attPoints)
            {
                if (m_isDeleted)
                    return;

                UUID itemID = m_appearance.GetAttachedItem(p);
                UUID assetID = m_appearance.GetAttachedAsset(p);

                // For some reason assetIDs are being written as Zero's in the DB -- need to track tat down
                // But they're not used anyway, the item is being looked up for now, so let's proceed.
                //if (UUID.Zero == assetID) 
                //{
                //    m_log.DebugFormat("[ATTACHMENT]: Cannot rez attachment in point {0} with itemID {1}", p, itemID);
                //    continue;
                //}

                try
                {
                    // Rez from inventory
                    UUID asset 
                        = m_scene.AttachmentsModule.RezSingleAttachmentFromInventory(ControllingClient, itemID, (uint)p);

                    m_log.InfoFormat(
                        "[ATTACHMENT]: Rezzed attachment in point {0} from item {1} and asset {2} ({3})",
                        p, itemID, assetID, asset);
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[ATTACHMENT]: Unable to rez attachment: {0}", e.ToString());
                }
            }
        }

        private void ReprioritizeUpdates()
        {
            if (Scene.IsReprioritizationEnabled && Scene.UpdatePrioritizationScheme != UpdatePrioritizationSchemes.Time)
            {
                lock (m_reprioritization_timer)
                {
                    if (!m_reprioritizing)
                        m_reprioritization_timer.Enabled = m_reprioritizing = true;
                    else
                        m_reprioritization_called = true;
                }
            }
        }

        private void Reprioritize(object sender, ElapsedEventArgs e)
        {
            m_controllingClient.ReprioritizeUpdates();

            lock (m_reprioritization_timer)
            {
                m_reprioritization_timer.Enabled = m_reprioritizing = m_reprioritization_called;
                m_reprioritization_called = false;
            }
        }
    }
}
