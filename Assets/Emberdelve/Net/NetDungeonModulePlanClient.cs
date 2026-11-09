using System;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Interaction;
using Emberdelve.Level;
using Emberdelve.Net.Rules;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Emberdelve.Net
{
    /// <summary>Receives the authority's map snapshot, applies it to the loaded modular scene and acknowledges the exact hash.</summary>
    internal sealed class NetDungeonModulePlanClient : MonoBehaviour
    {
        internal const string PlanMessage = "emberdelve.map-plan.v1";
        internal const string ReadyMessage = "emberdelve.map-ready.v1";

        private NetworkManager _network;
        private DungeonModuleCatalog _catalog;
        private DungeonModulePlan _pending;
        private bool _readySent;
        private bool _handlerRegistered;
        private float _applyDeadline;
        private float _nextApplyAttemptAt;
        private bool _applyRetryLogged;
        private string _lastApplyFailure;

        public void Configure(NetworkManager network)
        {
            _network = network;
        }

        private void Start()
        {
            _catalog = Resources.Load<DungeonModuleCatalog>("Emberdelve/DungeonModuleCatalog");
            SceneManager.sceneLoaded += OnSceneLoaded;
            RegisterHandlerWhenReady();
        }

        private void Update()
        {
            RegisterHandlerWhenReady();
            if (_pending != null && !_readySent) TryApplyAndAcknowledge();
        }

        private void RegisterHandlerWhenReady()
        {
            if (_handlerRegistered || _network == null || _network.CustomMessagingManager == null) return;
            _network.CustomMessagingManager.RegisterNamedMessageHandler(PlanMessage, OnPlanMessage);
            _handlerRegistered = true;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (_handlerRegistered && _network != null && _network.CustomMessagingManager != null)
                _network.CustomMessagingManager.UnregisterNamedMessageHandler(PlanMessage);
        }

        private void OnPlanMessage(ulong senderClientId, FastBufferReader reader)
        {
            if (senderClientId != NetworkManager.ServerClientId)
            {
                Debug.LogError(NetTuning.LogPrefix + "[client] rejected map plan from non-server sender=" + senderClientId);
                return;
            }

            byte[] payload = new byte[DungeonModulePlanCodec.PayloadBytes];
            // A rejected/replayed packet must never leave a previous plan eligible for a later positive ACK.
            _pending = null;
            _readySent = true;
            try { reader.ReadBytesSafe(ref payload, payload.Length); }
            catch (Exception e)
            {
                Debug.LogError(NetTuning.LogPrefix + "[client] map plan read failed: " + e.Message);
                SendReady(0, false);
                return;
            }

            string reason = _catalog == null ? "module catalog missing" : string.Empty;
            if (_catalog == null || !DungeonModulePlanCodec.TryDecode(payload, _catalog.Modules, out _pending, out reason))
            {
                _pending = null;
                Debug.LogError(NetTuning.LogPrefix + "[client] map plan rejected: " + reason);
                SendReady(0, false);
                return;
            }

            _readySent = false;
            _applyDeadline = Time.unscaledTime + 60f;
            _nextApplyAttemptAt = 0f;
            _applyRetryLogged = false;
            _lastApplyFailure = string.Empty;
            try { DungeonModulePlanSession.Install(_pending); }
            catch (Exception e)
            {
                Debug.LogError(NetTuning.LogPrefix + "[client] map plan install failed: " + e.Message);
                SendReady(0, false);
                return;
            }

            Debug.Log(NetTuning.LogPrefix + "[client] map plan received seed=" + _pending.Seed + " hash=" + _pending.LayoutHash);
            TryApplyAndAcknowledge();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == NetTuning.ModularDungeonSceneName) TryApplyAndAcknowledge();
        }

        private void TryApplyAndAcknowledge()
        {
            if (_readySent || _pending == null) return;
            if (Time.unscaledTime >= _applyDeadline)
            {
                _readySent = true;
                SendReady(0, false);
                Debug.LogError(NetTuning.LogPrefix + "[client] modular map readiness timed out: " +
                               (_lastApplyFailure.Length > 0 ? _lastApplyFailure : "scene did not become ready"));
                return;
            }
            if (Time.unscaledTime < _nextApplyAttemptAt) return;
            if (SceneManager.GetActiveScene().name != NetTuning.ModularDungeonSceneName) return;
            if (!DungeonModulePlanSession.TryApplyToLoadedScene(out string reason))
            {
                _lastApplyFailure = reason;
                if (!DungeonModuleSceneAssembler.IsTransientInitializationFailure(reason))
                {
                    _readySent = true;
                    SendReady(0, false);
                    Debug.LogError(NetTuning.LogPrefix + "[client] modular scene rejected its plan: " + reason);
                    return;
                }

                _nextApplyAttemptAt = Time.unscaledTime + 0.25f;
                if (!_applyRetryLogged)
                {
                    _applyRetryLogged = true;
                    Debug.LogWarning(NetTuning.LogPrefix + "[client] modular scene initialization is not ready; retrying plan apply: " + reason);
                }
                return;
            }

            if (!uint.TryParse(_pending.LayoutHash, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint hash))
            {
                _readySent = true;
                SendReady(0, false);
                return;
            }

            // NetSceneRoles runs after the assembler Awake and may switch roots off. Check the actual
            // post-role client scene immediately before acknowledging readiness.
            ExtractionPoint extraction = FindFirstObjectByType<ExtractionPoint>(FindObjectsInactive.Include);
            if (extraction == null || !extraction.isActiveAndEnabled || !extraction.gameObject.activeInHierarchy)
            {
                _lastApplyFailure = "extraction interactable is inactive after scene-role setup";
                _nextApplyAttemptAt = Time.unscaledTime + 0.25f;
                if (!_applyRetryLogged)
                {
                    _applyRetryLogged = true;
                    Debug.LogWarning(NetTuning.LogPrefix + "[client] extraction is not active after scene-role setup; retrying before ACK");
                }
                return;
            }
            _readySent = true;
            SendReady(hash, true);
            Debug.Log(NetTuning.LogPrefix + "[client] map ready hash=" + _pending.LayoutHash + " extractionActive=true");
        }

        private void SendReady(uint hash, bool success)
        {
            if (_network == null || !_network.IsConnectedClient || _network.CustomMessagingManager == null) return;
            byte[] payload = ModulePlanAckCodec.Encode(hash, success);
            using (FastBufferWriter writer = new FastBufferWriter(payload.Length, Allocator.Temp))
            {
                writer.WriteBytesSafe(payload, payload.Length);
                _network.CustomMessagingManager.SendNamedMessage(ReadyMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }
    }
}
