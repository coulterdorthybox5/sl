using UnityEngine;

[AddComponentMenu("Breakable/BreakableRigid")]
public class BreakableRigid : MonoBehaviour
{
    public float Health = 40f;
    public float ExplosionRadius = 6f;
    public float ExplosionDamage = 40f;
    public float ExplosionForce = 500f;
    public float Mass = 10f;
    public float Drag = 0.05f;
    public float AngularDrag = 0.05f;
    public float LifetimeAfterBreak = 8f;

    public int SplitCount = 1;
    public float SplitLifetime = 5f;
    public float SplitForce = 250f;
}