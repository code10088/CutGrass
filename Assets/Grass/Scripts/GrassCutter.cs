using UnityEngine;
using UnityEngine.Serialization;

public class GrassCutter : MonoBehaviour
{
    private Vector3 lastPosition;
    private float lastRadius;
    private bool initialized;

    [SerializeField] private ProceduralGrass grassRenderer;
    [FormerlySerializedAs("cutSensitive")] [SerializeField] private float cutSensitivity = 0.1f;
    [FormerlySerializedAs("Radius"), Min(0f)] public float radius = 0.3f;

    private void Update()
    {
        if (grassRenderer == null)
        {
            return;
        }

        Vector3 currentPosition = transform.position;
        if (!initialized)
        {
            initialized = true;
            lastPosition = currentPosition;
            lastRadius = radius;
            grassRenderer.AddCutSegment(currentPosition, currentPosition, radius);
            return;
        }

        bool movedTooLittle = (lastPosition - currentPosition).sqrMagnitude < cutSensitivity * cutSensitivity;
        bool radiusChangedTooLittle = Mathf.Abs(lastRadius - radius) < cutSensitivity;
        if (movedTooLittle && radiusChangedTooLittle)
        {
            return;
        }

        grassRenderer.AddCutSegment(lastPosition, currentPosition, Mathf.Max(lastRadius, radius));
        lastPosition = currentPosition;
        lastRadius = radius;
    }
}
