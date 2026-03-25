using UnityEngine;
using UnityEngine.Serialization;

public class GrassColliderBender : MonoBehaviour
{
    [SerializeField] private ProceduralGrass grassRenderer;
    [FormerlySerializedAs("Radius"), Min(0f)] public float radius = 0.5f;

    private void Update()
    {
        if (grassRenderer == null)
        {
            return;
        }

        grassRenderer.AddCollider(transform.position, radius);
    }
}
