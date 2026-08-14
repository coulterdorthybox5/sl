using UnityEngine;

[AddComponentMenu("Breakable/Damaging")]
public class Damaging : MonoBehaviour
{
    public float DamagePerSecond = 5f;
    public float TickInterval = 1f;

    public string EffectName = "";
    public float EffectSeconds = 3f;
    public int EffectIntensity = 1;

    public bool ShowMessage = false;
    public string Broadcast = "";
    public int BroadcastSeconds = 3;
}