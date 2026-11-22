using Emberdelve.Core;
using Emberdelve.Interaction;
using Emberdelve.Player;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>Local interaction marker. It sends only a request; the server still validates and answers.</summary>
    [DisallowMultipleComponent]
    public sealed class CorpseWorldInteractable : MonoBehaviour, IInteractable
    {
        private CorpseLootWorldPresenter _presenter;
        private Material _ownedMaterial;

        public ulong CorpseId { get; private set; }
        public ulong Revision { get; private set; }
        public string PromptText => "Search corpse";
        public bool RequiresHold => false;
        public float HoldDuration => 0f;

        public void Configure(CorpseLootWorldPresenter presenter, ulong corpseId)
        {
            _presenter = presenter;
            CorpseId = corpseId;
        }

        public void SetPresence(Vector3 position, ulong revision)
        {
            Revision = revision;
            transform.position = position + Vector3.up * 0.25f;
        }

        public void SetOwnedMaterial(Material material) => _ownedMaterial = material;

        public bool CanInteract(IInteractionActor actor, out string blockedReason)
        {
            if (_presenter == null)
            {
                blockedReason = "Corpse search is unavailable";
                return false;
            }
            return _presenter.CanInteract(actor, CorpseId, out blockedReason);
        }

        public void OnInteractInstant(IInteractionActor actor)
        {
            if (!CanInteract(actor, out string reason))
            {
                GameEvents.RaiseToast(actor != null ? actor.PlayerId : GameEvents.NoPlayer, reason);
                return;
            }
            if (!_presenter.OpenContainer(CorpseId))
                GameEvents.RaiseToast(actor.PlayerId, "Could not open corpse container");
        }

        public void OnHoldComplete(IInteractionActor actor) => OnInteractInstant(actor);

        private void OnDestroy()
        {
            if (_ownedMaterial != null) Destroy(_ownedMaterial);
        }
    }
}
