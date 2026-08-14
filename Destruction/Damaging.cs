using System.Collections.Generic;
using Exiled.API.Enums;
using Exiled.API.Features;
using UnityEngine;

namespace MainCore.Destruction
{
    public sealed class Damaging : MonoBehaviour
    {
        public float DamagePerSecond = 5f;
        public float TickInterval = 1f;

        public string EffectName = string.Empty;
        public float EffectSeconds = 3f;
        public byte EffectIntensity = 1;

        public bool ShowMessage = false;
        public string Broadcast = string.Empty;
        public ushort BroadcastSeconds = 3;

        private readonly Dictionary<ReferenceHub, float> nextTick = new Dictionary<ReferenceHub, float>();

        public void Configure(float dps, float tick, string effect, float effectSeconds, byte effectIntensity,
            bool showMessage, string broadcast, ushort broadcastSeconds)
        {
            DamagePerSecond = dps;
            TickInterval = Mathf.Max(0.05f, tick);
            EffectName = effect ?? string.Empty;
            EffectSeconds = effectSeconds;
            EffectIntensity = effectIntensity;
            ShowMessage = showMessage;
            Broadcast = broadcast ?? string.Empty;
            BroadcastSeconds = broadcastSeconds;
        }

        private void Awake()
        {
            Collider col = GetComponent<Collider>();
            if (col == null)
                col = gameObject.AddComponent<BoxCollider>();

            col.isTrigger = true;
        }

        private void OnTriggerStay(Collider other)
        {
            ReferenceHub? hub = other.GetComponentInParent<ReferenceHub>();
            if (hub == null)
                return;

            Player? player = Player.Get(hub);
            if (player == null || !player.IsAlive)
                return;

            float now = Time.unscaledTime;
            if (nextTick.TryGetValue(hub, out float next) && now < next)
                return;

            nextTick[hub] = now + TickInterval;

            if (DamagePerSecond > 0f)
            {
                try { player.Hurt(DamagePerSecond * TickInterval); }
                catch { }
            }

            if (!string.IsNullOrEmpty(EffectName) && EffectIntensity > 0)
            {
                if (System.Enum.TryParse(EffectName, true, out EffectType effect))
                {
                    try { player.EnableEffect(effect, EffectIntensity, EffectSeconds); }
                    catch { }
                }
            }

            if (ShowMessage && !string.IsNullOrEmpty(Broadcast))
            {
                try { player.Broadcast(BroadcastSeconds, Broadcast); }
                catch { }
            }
        }

        private void OnTriggerExit(Collider other)
        {
            ReferenceHub? hub = other.GetComponentInParent<ReferenceHub>();
            if (hub != null)
                nextTick.Remove(hub);
        }
    }
}