using UnityEngine;

[System.Serializable]
public class GrassBladeSettings
{
    [Header("形状参数")]
    [Range(1, 5)] public int segmentCount = 3;
    [Range(0.1f, 2f)] public float height = 0.5f;
    [Range(0.01f, 0.5f)] public float width = 0.1f;
    [Range(0f, 1f)] public float bendAmount = 0.3f;
    [Range(0f, 1f)] public float topNarrow = 0.3f;

    [Header("UV 设置")]
    public Vector2 uvScale = Vector2.one;
    public bool useVerticalUV = true;
}
