using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;

public class ProceduralGrass : MonoBehaviour
{
    private const int MaxColliders = 8;
    private const float CutMaskSaveInterval = 0.25f;

    private struct CutSegment
    {
        public Vector3 start;
        public Vector3 end;
        public float radius;

        public CutSegment(Vector3 start, Vector3 end, float radius)
        {
            this.start = start;
            this.end = end;
            this.radius = radius;
        }
    }

    [Header("草地设置")]
    public GrassBladeSettings grassSettings = new GrassBladeSettings();

    [Header("生成坐标范围")]
    public Vector2 spawnRangeX = new Vector2(-5f, 5f);
    public Vector2 spawnRangeZ = new Vector2(-5f, 5f);

    [Header("生成贴图")]
    public ComputeShader grassPositionCompute;
    public ComputeShader grassCutCompute;
    [FormerlySerializedAs("tex1")] public Texture2D spawnMask;
    [Min(0.01f)] public float gridSize = 0.1f;
    [Range(0f, 1f)] public float texThreshold = 0.5f;

    [Header("风设置")]
    public float windStrength = 0.5f;
    public float windFrequency = 0.5f;
    public Vector3 windDirection = Vector3.right;

    [Header("渲染")]
    public Material grassMaterial;
    public ShadowCastingMode shadowMode = ShadowCastingMode.On;
    public bool receiveShadows = true;

    private Mesh mesh;
    private ComputeBuffer positionBuffer;
    private ComputeBuffer argsBuffer;
    private MaterialPropertyBlock props;
    private int grassPositionKernel;
    private int grassCutKernel;
    private int gridCountX;
    private int gridCountZ;
    private int colliderCount;
    private Vector2 spawnMinXZ;
    private Vector2 spawnMaxXZ;
    private ComputeBuffer cutStateBuffer;
    private bool cutMaskDirty;
    private float nextCutMaskSaveTime;
    private readonly Vector4[] colliderData = new Vector4[MaxColliders];
    private readonly List<CutSegment> pendingCuts = new List<CutSegment>();

    private Bounds GetSpawnBounds()
    {
        float minX = Mathf.Min(spawnRangeX.x, spawnRangeX.y);
        float maxX = Mathf.Max(spawnRangeX.x, spawnRangeX.y);
        float minZ = Mathf.Min(spawnRangeZ.x, spawnRangeZ.y);
        float maxZ = Mathf.Max(spawnRangeZ.x, spawnRangeZ.y);

        Vector3 center = new Vector3((minX + maxX) * 0.5f, transform.position.y + 5f, (minZ + maxZ) * 0.5f);
        Vector3 size = new Vector3(Mathf.Max(maxX - minX, 0.1f), 10f, Mathf.Max(maxZ - minZ, 0.1f));
        return new Bounds(center, size);
    }

    private void Start()
    {
        if (grassMaterial == null || grassPositionCompute == null)
        {
            return;
        }
        mesh = GrassMeshGenerator.CreateGrassBlade(grassSettings);
        grassMaterial.enableInstancing = true;
        ReleaseBuffers();

        float minX = Mathf.Min(spawnRangeX.x, spawnRangeX.y);
        float maxX = Mathf.Max(spawnRangeX.x, spawnRangeX.y);
        float minZ = Mathf.Min(spawnRangeZ.x, spawnRangeZ.y);
        float maxZ = Mathf.Max(spawnRangeZ.x, spawnRangeZ.y);
        spawnMinXZ = new Vector2(minX, minZ);
        spawnMaxXZ = new Vector2(maxX, maxZ);

        gridCountX = Mathf.Max(1, Mathf.FloorToInt((maxX - minX) / gridSize) + 1);
        gridCountZ = Mathf.Max(1, Mathf.FloorToInt((maxZ - minZ) / gridSize) + 1);
        int maxGrassCount = gridCountX * gridCountZ;

        positionBuffer = new ComputeBuffer(maxGrassCount, 16, ComputeBufferType.Append);
        positionBuffer.SetCounterValue(0);

        uint[] args = new uint[5];
        args[0] = mesh.GetIndexCount(0);
        args[1] = 0;
        args[2] = mesh.GetIndexStart(0);
        args[3] = mesh.GetBaseVertex(0);

        argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        argsBuffer.SetData(args);

        grassPositionKernel = grassPositionCompute.FindKernel("CSMain");
        grassPositionCompute.SetVector("_SpawnMinXZ", new Vector4(minX, minZ, 0f, 0f));
        grassPositionCompute.SetVector("_SpawnMaxXZ", new Vector4(maxX, maxZ, 0f, 0f));
        grassPositionCompute.SetFloat("_GridSize", gridSize);
        grassPositionCompute.SetFloat("_Threshold", texThreshold);
        grassPositionCompute.SetFloat("_GrassHeight", grassSettings.height);
        grassPositionCompute.SetInt("_GridCountX", gridCountX);
        grassPositionCompute.SetInt("_GridCountZ", gridCountZ);
        grassPositionCompute.SetInt("_UseSpawnMask", spawnMask != null ? 1 : 0);
        if (spawnMask != null)
        {
            grassPositionCompute.SetTexture(grassPositionKernel, "_SpawnMask", spawnMask);
        }
        grassPositionCompute.SetBuffer(grassPositionKernel, "_GrassPositions", positionBuffer);

        SetupCutStateBuffer();
        if (cutStateBuffer != null)
        {
            grassPositionCompute.SetBuffer(grassPositionKernel, "_CutStates", cutStateBuffer);
        }

        props = new MaterialPropertyBlock();
        props.SetBuffer("_PositionBuffer", positionBuffer);
        props.SetInt("_ColliderCount", 0);
        props.SetVectorArray("_Colliders", colliderData);
        ApplyWindProperties();
    }

    private void LateUpdate()
    {
        if (positionBuffer == null || argsBuffer == null || grassMaterial == null)
        {
            return;
        }

        ApplyPendingCuts();
        SaveCutMaskIfNeeded();
        UpdateVisibleGrassPositions();
        UpdateColliderData();
        ApplyWindProperties();

        Graphics.DrawMeshInstancedIndirect(
            mesh,
            0,
            grassMaterial,
            GetSpawnBounds(),
            argsBuffer,
            0,
            props,
            shadowMode,
            receiveShadows,
            0,
            null
        );
    }

    private void ApplyWindProperties()
    {
        if (props == null)
        {
            return;
        }

        Vector3 normalizedDirection = windDirection.sqrMagnitude > 0.0001f ? windDirection.normalized : Vector3.right;
        props.SetFloat("_WindStrength", windStrength);
        props.SetFloat("_WindFrequency", windFrequency);
        props.SetVector("_WindDirection", normalizedDirection);
    }

    private void SetupCutStateBuffer()
    {
        int cutStateCount = gridCountX * gridCountZ;
        cutStateBuffer = new ComputeBuffer(cutStateCount, sizeof(uint));

        uint[] initialStates = new uint[cutStateCount];
        if (!LoadCutStateFromDisk(initialStates))
        {
            for (int i = 0; i < initialStates.Length; i++)
            {
                initialStates[i] = 1;
            }
        }

        cutStateBuffer.SetData(initialStates);

        if (grassCutCompute == null)
        {
            return;
        }

        grassCutKernel = grassCutCompute.FindKernel("CSMain");
        grassCutCompute.SetBuffer(grassCutKernel, "_CutStates", cutStateBuffer);
        grassCutCompute.SetVector("_SpawnMinXZ", new Vector4(spawnMinXZ.x, spawnMinXZ.y, 0f, 0f));
        grassCutCompute.SetFloat("_GridSize", gridSize);
        grassCutCompute.SetInt("_GridCountX", gridCountX);
        grassCutCompute.SetInt("_GridCountZ", gridCountZ);
    }

    private string GetCutMaskFilePath()
    {
        string sceneName = SceneManager.GetActiveScene().name;
        string objectName = gameObject.name.Replace(' ', '_');
        string fileName = $"{sceneName}_{objectName}_cutmask.bin";
        return Path.Combine(Application.persistentDataPath, fileName);
    }

    private bool LoadCutStateFromDisk(uint[] targetStates)
    {
        if (targetStates == null || targetStates.Length != gridCountX * gridCountZ)
        {
            return false;
        }

        string filePath = GetCutMaskFilePath();
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using (BinaryReader reader = new BinaryReader(File.OpenRead(filePath)))
            {
                int width = reader.ReadInt32();
                int height = reader.ReadInt32();
                int bitsetByteCount = reader.ReadInt32();

                int cellCount = width * height;
                int expectedByteCount = (cellCount + 7) / 8;
                if (width != gridCountX || height != gridCountZ || bitsetByteCount != expectedByteCount)
                {
                    return false;
                }

                byte[] bytes = reader.ReadBytes(bitsetByteCount);
                if (bytes.Length != bitsetByteCount)
                {
                    return false;
                }

                for (int i = 0; i < targetStates.Length; i++)
                {
                    int byteIndex = i >> 3;
                    int bitIndex = i & 7;
                    targetStates[i] = ((bytes[byteIndex] >> bitIndex) & 1) != 0 ? 1u : 0u;
                }

                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private void SaveCutMaskIfNeeded()
    {
        if (!cutMaskDirty || cutStateBuffer == null || Time.unscaledTime < nextCutMaskSaveTime)
        {
            return;
        }

        SaveCutStateToDisk();
    }

    private void SaveCutStateToDisk()
    {
        if (cutStateBuffer == null || (!cutMaskDirty && !File.Exists(GetCutMaskFilePath())))
        {
            return;
        }

        uint[] stateData = new uint[gridCountX * gridCountZ];
        cutStateBuffer.GetData(stateData);
        byte[] bytes = new byte[(stateData.Length + 7) / 8];
        for (int i = 0; i < stateData.Length; i++)
        {
            if (stateData[i] > 0)
            {
                int byteIndex = i >> 3;
                int bitIndex = i & 7;
                bytes[byteIndex] |= (byte)(1 << bitIndex);
            }
        }

        string filePath = GetCutMaskFilePath();
        using (BinaryWriter writer = new BinaryWriter(File.Open(filePath, FileMode.Create, FileAccess.Write, FileShare.None)))
        {
            writer.Write(gridCountX);
            writer.Write(gridCountZ);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        cutMaskDirty = false;
    }

    private void ApplyPendingCuts()
    {
        if (grassCutCompute == null || cutStateBuffer == null || pendingCuts.Count == 0)
        {
            return;
        }

        int threadGroupX = Mathf.CeilToInt(gridCountX / 8f);
        int threadGroupZ = Mathf.CeilToInt(gridCountZ / 8f);

        for (int i = 0; i < pendingCuts.Count; i++)
        {
            CutSegment cut = pendingCuts[i];
            grassCutCompute.SetVector("_CutStart", cut.start);
            grassCutCompute.SetVector("_CutEnd", cut.end);
            grassCutCompute.SetFloat("_CutRadius", cut.radius);
            grassCutCompute.Dispatch(grassCutKernel, threadGroupX, threadGroupZ, 1);
        }

        pendingCuts.Clear();
        cutMaskDirty = true;
        nextCutMaskSaveTime = Time.unscaledTime + CutMaskSaveInterval;
    }

    private void UpdateColliderData()
    {
        if (props == null)
        {
            return;
        }

        props.SetInt("_ColliderCount", colliderCount);
        props.SetVectorArray("_Colliders", colliderData);
        colliderCount = 0;
    }

    public bool AddCollider(Vector3 position, float radius)
    {
        if (colliderCount >= MaxColliders)
        {
            return false;
        }

        colliderData[colliderCount++] = new Vector4(position.x, position.y, position.z, radius);
        return true;
    }

    public void AddCutSegment(Vector3 start, Vector3 end, float radius)
    {
        pendingCuts.Add(new CutSegment(start, end, radius));
    }

    private void UpdateVisibleGrassPositions()
    {
        Camera targetCamera = Camera.main;
        if (targetCamera == null)
        {
            positionBuffer.SetCounterValue(0);
            ComputeBuffer.CopyCount(positionBuffer, argsBuffer, sizeof(uint));
            return;
        }

        Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(targetCamera.projectionMatrix, false);
        Matrix4x4 viewProj = gpuProjection * targetCamera.worldToCameraMatrix;

        positionBuffer.SetCounterValue(0);
        grassPositionCompute.SetMatrix("_ViewProj", viewProj);

        int threadGroupX = Mathf.CeilToInt(gridCountX / 8f);
        int threadGroupZ = Mathf.CeilToInt(gridCountZ / 8f);
        grassPositionCompute.Dispatch(grassPositionKernel, threadGroupX, threadGroupZ, 1);
        ComputeBuffer.CopyCount(positionBuffer, argsBuffer, sizeof(uint));
    }

    private void OnDisable()
    {
        SaveCutStateToDisk();
        ReleaseBuffers();
    }

    private void OnApplicationQuit()
    {
        SaveCutStateToDisk();
    }

    private void ReleaseBuffers()
    {
        positionBuffer?.Release();
        positionBuffer = null;

        argsBuffer?.Release();
        argsBuffer = null;

        cutStateBuffer?.Release();
        cutStateBuffer = null;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.green;
        Bounds spawnBounds = GetSpawnBounds();
        Gizmos.DrawWireCube(spawnBounds.center, new Vector3(spawnBounds.size.x, 1f, spawnBounds.size.z));

        Gizmos.color = Color.cyan;
        Vector3 direction = windDirection.sqrMagnitude > 0.0001f ? windDirection.normalized : Vector3.right;
        Vector3 windStart = transform.position;
        Vector3 windEnd = windStart + direction * 3f;
        Gizmos.DrawLine(windStart, windEnd);
        UnityEditor.Handles.ArrowHandleCap(
            0,
            windStart,
            Quaternion.LookRotation(direction),
            2f,
            EventType.Repaint
        );
    }
#endif
}
