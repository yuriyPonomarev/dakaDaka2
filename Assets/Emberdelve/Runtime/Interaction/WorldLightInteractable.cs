using System.Collections.Generic;
using System.Text;
using Emberdelve.Core;
using Emberdelve.Level;
using UnityEngine;

namespace Emberdelve.Interaction
{
    public interface IWorldLightAuthority
    {
        bool LightStateReady { get; }
        bool RequestLightState(IInteractionActor actor, ulong id, bool lit);
    }

    /// <summary>Interaction proxy stays enabled when its flame/light is extinguished.</summary>
    public sealed class WorldLightInteractable : MonoBehaviour, IInteractable
    {
        public static readonly List<WorldLightInteractable> Active = new List<WorldLightInteractable>();
        public const float Reach = 3f;
        private Light _source;
        private Renderer[] _glow;
        private ParticleSystem[] _particles;
        public ulong StableId { get; private set; }
        public bool IsLit { get; private set; } = true;
        public string PromptText => IsLit ? "Extinguish light" : "Light fire";
        public bool RequiresHold => false;
        public float HoldDuration => 0f;

        private void OnEnable() { if (!Active.Contains(this)) Active.Add(this); }
        private void OnDisable() { Active.Remove(this); }

        public static void Install(Transform moduleRoot)
        {
            foreach (var light in moduleRoot.GetComponentsInChildren<Light>(true))
            {
                if (light.type == LightType.Directional || light.GetComponentInChildren<WorldLightInteractable>(true) != null) continue;
                var proxy = new GameObject("WorldLightSwitch");
                proxy.transform.SetParent(light.transform, false);
                proxy.layer = LayerMask.NameToLayer(GameConfig.InteractLayerName);
                var interaction = proxy.AddComponent<WorldLightInteractable>();
                var trigger = proxy.AddComponent<SphereCollider>();
                trigger.isTrigger = true;
                trigger.radius = .35f;
                interaction.Configure(light);
            }
        }

        public void Configure(Light source)
        {
            _source = source;
            StableId = Identity(source.transform);
            IsLit = source.enabled;
            Emberdelve.Feedback.WorldLightAppearance.Configure(source);
            var glow = new List<Renderer>();
            foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
            {
                string name = renderer.name.ToLowerInvariant();
                if (name.Contains("flame") || name.Contains("coal") || name.Contains("glow") || name.Contains("halo") || name.Contains("volume") || name.Contains("haze")) glow.Add(renderer);
            }
            // Existing braziers author their flame beside the light, rather than under it.
            if (source.transform.parent != null)
                foreach (var renderer in source.transform.parent.GetComponentsInChildren<Renderer>(true))
                    if (renderer.name == "Brazier_Flame" && Vector3.SqrMagnitude(renderer.transform.position - source.transform.position) < .25f && !glow.Contains(renderer)) glow.Add(renderer);
            _glow = glow.ToArray();
            foreach (var renderer in _glow) renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _particles = source.GetComponentsInChildren<ParticleSystem>(true);
            ApplyVisuals();
        }

        public static ulong Identity(Transform source)
        {
            var path = new StringBuilder();
            for (Transform t = source; t != null; t = t.parent)
            {
                if (t.GetComponent<DungeonModuleDefinition>() != null) { path.Insert(0, "/" + t.name); break; }
                path.Insert(0, "/" + t.name + ":" + t.GetSiblingIndex());
            }
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                foreach (char c in path.ToString()) { hash ^= c; hash *= 1099511628211UL; }
                return hash;
            }
        }

        public bool CanInteract(IInteractionActor actor, out string reason)
        {
            if (NetRole.IsClientOnly && (!GameServices.TryGet<IWorldLightAuthority>(out var service) || !service.LightStateReady))
            { reason = "Light state is syncing"; return false; }
            return InteractionRules.Validate(actor, this, Reach, out reason);
        }

        public void OnInteractInstant(IInteractionActor actor)
        {
            if (!CanInteract(actor, out string reason)) { GameEvents.RaiseToast(actor != null ? actor.PlayerId : GameEvents.NoPlayer, reason); return; }
            if (NetRole.IsNetworked)
            {
                if (!GameServices.TryGet<IWorldLightAuthority>(out var service) || !service.RequestLightState(actor, StableId, !IsLit))
                    GameEvents.RaiseToast(actor.PlayerId, "Waiting for the server");
            }
            else TrySetAuthoritatively(actor, !IsLit, out _);
        }
        public void OnHoldComplete(IInteractionActor actor) => OnInteractInstant(actor);

        public bool TrySetAuthoritatively(IInteractionActor actor, bool lit, out string reason)
        {
            if (!NetRole.RunsSimulation) { reason = "Server authority required"; return false; }
            if (!InteractionRules.Validate(actor, this, Reach, out reason)) return false;
            SetLit(lit);
            return true;
        }
        // Network channel verifies sender and current layout before calling this.
        public void ApplyReplicatedState(bool lit) { if (NetRole.IsClientOnly) SetLit(lit); }
        private void SetLit(bool lit)
        {
            if (IsLit == lit) return;
            IsLit = lit;
            ApplyVisuals();
            Debug.Log("[WorldLight] state id=" + StableId + " lit=" + lit + " at=" + transform.position);
        }
        private void ApplyVisuals()
        {
            if (_source != null) _source.enabled = IsLit;
            if (_glow != null) foreach (var renderer in _glow) if (renderer != null) renderer.enabled = IsLit;
            if (_particles != null) foreach (var particles in _particles) if (particles != null)
            {
                if (IsLit) particles.Play(true); else particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }
    }
}
