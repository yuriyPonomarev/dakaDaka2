using UnityEngine;

namespace Emberdelve.Core
{
    /// <summary>
    /// U3A2: decides, per network role, which parts of the authored dungeon scene exist in this process. The dungeon scene
    /// is authored once for the offline game; online, the same scene is loaded by every process and this component switches
    /// the offline-only pieces off before their Awake can run (execution order is very early, and a GameObject deactivated
    /// by an earlier Awake never gets its own).
    ///
    /// <list type="bullet">
    /// <item>Offline: a no-op. Nothing is deactivated, so the offline game is exactly what the builder authored.</item>
    /// <item>Any networked role: <see cref="deactivateWhenNetworked"/> goes away (the scene's own player rig: networked
    /// players are spawned from the network player prefab instead).</item>
    /// <item>Client (view only): additionally <see cref="deactivateOnClient"/> (the game master with the combat authority,
    /// run director and HUD, plus enemies, loot and the extraction gate). The client is a view of the server's world.</item>
    /// <item>--ember-net-no-enemies (NetRole.EnemiesDisabled, proof scenarios): the "Enemies" root is also deactivated on a
    /// simulating process.</item>
    /// <item>Dedicated server (no screen): <see cref="disableOnDedicatedServer"/> components are disabled before they run
    /// (HUD, result screen, feedback), so a headless process builds no UI.</item>
    /// </list>
    /// It lives in the Runtime assembly (it needs only <see cref="NetRole"/>), so the level builder can add it without
    /// referencing the network assembly or any package. The scene wiring is done by EmberdelveLevelBuilder.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    [DisallowMultipleComponent]
    public sealed class NetSceneRoles : MonoBehaviour
    {
        [SerializeField] private GameObject[] deactivateWhenNetworked;
        [SerializeField] private GameObject[] deactivateOnClient;
        [SerializeField] private Behaviour[] disableOnDedicatedServer;

        public GameObject[] DeactivateWhenNetworked => deactivateWhenNetworked;
        public GameObject[] DeactivateOnClient => deactivateOnClient;
        public Behaviour[] DisableOnDedicatedServer => disableOnDedicatedServer;

        /// <summary>Builder / test hook: the three lists, in the order of the fields above.</summary>
        public void Configure(GameObject[] networked, GameObject[] client, Behaviour[] dedicatedServer)
        {
            deactivateWhenNetworked = networked;
            deactivateOnClient = client;
            disableOnDedicatedServer = dedicatedServer;
        }

        private void Awake()
        {
            Apply(NetRole.Current);
        }

        /// <summary>One-way: applies the role's switch-offs. Offline changes nothing. Safe to call twice.</summary>
        public void Apply(NetRoleKind role)
        {
            if (role == NetRoleKind.Offline) return;

            Deactivate(deactivateWhenNetworked);
            if (role == NetRoleKind.Client) Deactivate(deactivateOnClient);
            else if (NetRole.EnemiesDisabled) DeactivateEnemies();

            if (role == NetRoleKind.DedicatedServer && disableOnDedicatedServer != null)
            {
                for (int i = 0; i < disableOnDedicatedServer.Length; i++)
                {
                    Behaviour behaviour = disableOnDedicatedServer[i];
                    if (behaviour != null) behaviour.enabled = false;
                }
            }
        }

        /// <summary>
        /// --ember-net-no-enemies on a simulating process: the scene's "Enemies" root goes away before its Awake (so no enemy
        /// registers as a combat entity). Found by name inside the client list, else by GameObject.Find; the scene is not re-authored.
        /// </summary>
        private void DeactivateEnemies()
        {
            bool done = false;
            if (deactivateOnClient != null)
            {
                for (int i = 0; i < deactivateOnClient.Length; i++)
                {
                    GameObject target = deactivateOnClient[i];
                    if (target != null && target.name == EnemiesRootName)
                    {
                        target.SetActive(false);
                        done = true;
                    }
                }
            }

            if (!done)
            {
                GameObject found = GameObject.Find(EnemiesRootName);
                if (found != null) found.SetActive(false);
            }
        }

        private const string EnemiesRootName = "Enemies";

        private static void Deactivate(GameObject[] targets)
        {
            if (targets == null) return;
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] != null) targets[i].SetActive(false);
            }
        }
    }
}
