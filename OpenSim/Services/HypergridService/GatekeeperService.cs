﻿/*
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

using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using OpenSim.Server.Base;

using OpenMetaverse;

using Nini.Config;
using log4net;

namespace OpenSim.Services.HypergridService
{
    public class GatekeeperService : IGatekeeperService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        IGridService m_GridService;
        IPresenceService m_PresenceService;
        IAuthenticationService m_AuthenticationService;
        IUserAccountService m_UserAccountService;
        ISimulationService m_SimulationService;

        string m_AuthDll;

        UUID m_ScopeID;
        bool m_AllowTeleportsToAnyRegion;
        GridRegion m_DefaultGatewayRegion;

        public GatekeeperService(IConfigSource config, ISimulationService simService)
        {
            IConfig serverConfig = config.Configs["GatekeeperService"];
            if (serverConfig == null)
                throw new Exception(String.Format("No section GatekeeperService in config file"));

            string accountService = serverConfig.GetString("UserAccountService", String.Empty);
            string gridService = serverConfig.GetString("GridService", String.Empty);
            string presenceService = serverConfig.GetString("PresenceService", String.Empty);
            string simulationService = serverConfig.GetString("SimulationService", String.Empty);

            m_AuthDll = serverConfig.GetString("AuthenticationService", String.Empty);

            if (accountService == string.Empty || gridService == string.Empty ||
                presenceService == string.Empty || m_AuthDll == string.Empty)
                throw new Exception("Incomplete specifications, Gatekeeper Service cannot function.");
            
            string scope = serverConfig.GetString("ScopeID", UUID.Zero.ToString());
            UUID.TryParse(scope, out m_ScopeID);
            //m_WelcomeMessage = serverConfig.GetString("WelcomeMessage", "Welcome to OpenSim!");
            m_AllowTeleportsToAnyRegion = serverConfig.GetBoolean("AllowTeleportsToAnyRegion", true);

            Object[] args = new Object[] { config };
            m_UserAccountService = ServerUtils.LoadPlugin<IUserAccountService>(accountService, args);
            m_GridService = ServerUtils.LoadPlugin<IGridService>(gridService, args);
            m_PresenceService = ServerUtils.LoadPlugin<IPresenceService>(presenceService, args);
            if (simService != null)
                m_SimulationService = simService;
            else if (simulationService != string.Empty)
                    m_SimulationService = ServerUtils.LoadPlugin<ISimulationService>(simulationService, args);

            if (m_UserAccountService == null || m_GridService == null ||
                m_PresenceService == null || m_SimulationService == null)
                throw new Exception("Unable to load a required plugin, Gatekeeper Service cannot function.");

            m_log.Debug("[GATEKEEPER SERVICE]: Starting...");
        }

        public GatekeeperService(IConfigSource config)
            : this(config, null)
        {
        }

        public bool LinkRegion(string regionName, out UUID regionID, out ulong regionHandle, out string imageURL, out string reason)
        {
            regionID = UUID.Zero;
            regionHandle = 0;
            imageURL = string.Empty;
            reason = string.Empty;

            m_log.DebugFormat("[GATEKEEPER SERVICE]: Request to link to {0}", regionName);
            if (!m_AllowTeleportsToAnyRegion)
            {
                List<GridRegion> defs = m_GridService.GetDefaultRegions(m_ScopeID);
                if (defs != null && defs.Count > 0)
                    m_DefaultGatewayRegion = defs[0];

                try
                {
                    regionID = m_DefaultGatewayRegion.RegionID;
                    regionHandle = m_DefaultGatewayRegion.RegionHandle;
                }
                catch
                {
                    reason = "Grid setup problem";
                    return false;
                }
                if (regionName != string.Empty)
                {
                    reason = "Direct links to regions not allowed";
                    return false;
                }

                return true;
            }

            GridRegion region = m_GridService.GetRegionByName(m_ScopeID, regionName);
            if (region == null)
            {
                reason = "Region not found";
                return false;
            }

            regionID = region.RegionID;
            regionHandle = region.RegionHandle;
            string regionimage = "regionImage" + region.RegionID.ToString();
            regionimage = regionimage.Replace("-", "");

            imageURL = "http://" + region.ExternalHostName + ":" + region.HttpPort + "/index.php?method=" + regionimage;

            return true;
        }

        public GridRegion GetHyperlinkRegion(UUID regionID)
        {
            m_log.DebugFormat("[GATEKEEPER SERVICE]: Request to get hyperlink region {0}", regionID);

            if (!m_AllowTeleportsToAnyRegion)
                // Don't even check the given regionID
                return m_DefaultGatewayRegion;

            GridRegion region = m_GridService.GetRegionByUUID(m_ScopeID, regionID);
            return region;
        }

        public bool LoginAgent(AgentCircuitData aCircuit, GridRegion destination, out string reason)
        {
            reason = string.Empty;

            string authURL = string.Empty;
            if (aCircuit.ServiceURLs.ContainsKey("HomeURI"))
                authURL = aCircuit.ServiceURLs["HomeURI"].ToString();
            m_log.DebugFormat("[GATEKEEPER SERVICE]: Request to login foreign agent {0} {1} @ {2} ({3}) at destination {4}", 
                aCircuit.firstname, aCircuit.lastname, authURL, aCircuit.AgentID, destination.RegionName);

            if (!Authenticate(aCircuit))
            {
                reason = "Unable to verify identity";
                m_log.InfoFormat("[GATEKEEPER SERVICE]: Unable to verify identity of agent {0} {1}. Refusing service.", aCircuit.firstname, aCircuit.lastname);
                return false;
            }
            m_log.DebugFormat("[GATEKEEPER SERVICE]: Identity verified for {0} {1} @ {2}", aCircuit.firstname, aCircuit.lastname, authURL);

            // Check to see if we have a local user with that UUID
            UserAccount account = m_UserAccountService.GetUserAccount(m_ScopeID, aCircuit.AgentID);
            if (account != null)
            {
                // No, sorry; go away
                reason = "User identifier not allowed on this grid";
                m_log.InfoFormat("[GATEKEEPER SERVICE]: Foreign agent {0} {1} has UUID of local user {3}. Refusing service.", 
                    aCircuit.firstname, aCircuit.lastname, aCircuit.AgentID);
                return false;
            }
            m_log.DebugFormat("[GATEKEEPER SERVICE]: User ID ok");

            // May want to authorize

            // Login the presence
            if (!m_PresenceService.LoginAgent(aCircuit.AgentID.ToString(), aCircuit.SessionID, aCircuit.SecureSessionID))
            {
                reason = "Unable to login presence";
                m_log.InfoFormat("[GATEKEEPER SERVICE]: Presence login failed for foreign agent {0} {1}. Refusing service.",
                    aCircuit.firstname, aCircuit.lastname);
                return false;
            }
            m_log.DebugFormat("[GATEKEEPER SERVICE]: Login presence ok");

            // Get the region
            destination = m_GridService.GetRegionByUUID(m_ScopeID, destination.RegionID);
            if (destination == null)
            {
                reason = "Destination region not found";
                return false;
            }
            m_log.DebugFormat("[GATEKEEPER SERVICE]: destination ok : {0}", destination.RegionName);

            // Finally launch the agent at the destination
            aCircuit.firstname = aCircuit.firstname + "." + aCircuit.lastname;
            aCircuit.lastname = "@" + aCircuit.ServiceURLs["HomeURI"].ToString();
            return m_SimulationService.CreateAgent(destination, aCircuit, 0, out reason);
        }

        public bool UpdateAgent(GridRegion destination, AgentData agent)
        {
            // Get the region
            destination = m_GridService.GetRegionByUUID(m_ScopeID, destination.RegionID);
            if (destination == null)
            {
                return false;
            }

            return m_SimulationService.UpdateAgent(destination, agent);
        }

        public bool LoginAttachment(GridRegion destination, ISceneObject sog)
        {
            // Get the region
            destination = m_GridService.GetRegionByUUID(m_ScopeID, destination.RegionID);
            if (destination == null)
            {
                return false;
            }

            // May want to filter attachments
            return m_SimulationService.CreateObject(destination, sog, false);
        }

        public void ReleaseAgent(UUID regionID, UUID agentID)
        {
            GridRegion region = m_GridService.GetRegionByUUID(m_ScopeID, regionID);
            if (region != null)
            {
                string uri = "http://" + region.ExternalHostName + ":" + region.HttpPort +
                    "/agent/" + agentID.ToString() + "/" + regionID.ToString() + "/release/";

                m_SimulationService.ReleaseAgent(regionID, agentID, uri);
            }
        }

        protected bool Authenticate(AgentCircuitData aCircuit)
        {
            string authURL = string.Empty;
            if (aCircuit.ServiceURLs.ContainsKey("HomeURI"))
                authURL = aCircuit.ServiceURLs["HomeURI"].ToString();

            if (authURL == string.Empty)
            {
                m_log.DebugFormat("[GATEKEEPER SERVICE]: Agent did not provide an authentication server URL");
                return false;
            }

            Object[] args = new Object[] { authURL };
            IAuthenticationService authService = ServerUtils.LoadPlugin<IAuthenticationService>(m_AuthDll, args);
            if (authService != null)
            {
                try
                {
                    return authService.Verify(aCircuit.AgentID, aCircuit.SecureSessionID.ToString(), 30);
                }
                catch
                {
                    m_log.DebugFormat("[GATEKEEPER SERVICE]: Unable to contact authentication service at {0}", authURL);
                    return false;
                }
            }

            return false;
        }
    }
}