using UnityEngine;

[AddComponentMenu("Breakable/BreakableNorm")]
public class BreakableNorm : MonoBehaviour
{
    public float Health = 40f;
    public float ExplosionRadius = 6f;
    public float ExplosionDamage = 40f;

    public int SplitCount = 1;
    public float SplitLifetime = 5f;
    public float SplitForce = 250f;
}