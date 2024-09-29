using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using System;
using UnityEngine.Rendering;
using System.Diagnostics;
using Unity.Mathematics;

struct Chunk
{
    public Vector3 position; //World position of chunk
    public int instanceStartIndex; //Start index inside trsBuffer
    public int instanceCount; //End index inside trsBuffer

    public Chunk(Vector3 p)
    {
        position = p;
        instanceStartIndex = -1;
        instanceCount = 0;
    }
};

public class GrassInstancerIndirect : MonoBehaviour
{

    [Header("Debugging")]
    [SerializeField] private bool _drawGizmos;
    [Header("Instancing")]
    [SerializeField] private int _instances;
    [SerializeField] private int _trueInstanceCount;
    [SerializeField] private int _visibleInstanceCount;
    [Header("Grass settings")]
    [SerializeField] private Vector2 _range;
    [SerializeField] private Vector3 _scaleMin = Vector3.one;
    [SerializeField] private Vector3 _scaleMax = Vector3.one;
    [SerializeField] private float _scaleNoiseScale = 0.0001f;
    [Header("Rendering")]
    [SerializeField] private Material _material;
    [SerializeField] private Mesh _mesh;
    [SerializeField] private bool _castShadows;
    [SerializeField] private bool _recieveShadows;
    [SerializeField] private float _maxViewDistance = 30f;
    [SerializeField] private ComputeShader _grassComputeShader;
    private int _kernelChunkRender;
    private int _kernelInitChunkInstanceCount;
    private int _kernelInitInstanceTransforms;
    private ComputeBuffer _visibleBuffer;
    private ComputeBuffer _argsBuffer;
    private ComputeBuffer _trsBuffer;
    private ComputeBuffer _readBackArgsBuffer;
    private ComputeBuffer _chunkBuffer;
    private ComputeBuffer _instanceCounterBuffer; // Needed to atomically count instances inside shader
    private Bounds _renderBounds;
    private Camera _cam;
    [Header("Chunks")]
    [SerializeField] private int _chunkSize = 4;
    private Chunk[] _chunks;
    private int _numChunks;
    [SerializeField] private uint _threadsChunkRender = 512;
    [Header("Occlusion")]
    private Texture _cameraDepthTexture;
    [SerializeField][Range(0.0001f, 0.1f)] private float _depthBias = 0.001f;
    [Header("Terrain")]
    [SerializeField] private Terrain _terrain;
    private TerrainData _terrainData;
    [SerializeField][Range(0f, 1f)] private float _grassThreshhold = 0.5f;
    [SerializeField] private int _threadsChunkInit = 512;

    private void Start()
    {
        _cam = Camera.main;
        _cam.depthTextureMode = DepthTextureMode.Depth;

        _terrainData = _terrain.terrainData;

        _trueInstanceCount = 0;
        _renderBounds = new Bounds(transform.position, Vector3.one * _range.x);

        InitializeGrassChunkPositions();
        InitializeGrassChunkInstances();
    }

    private void Update()
    {
        if (_cameraDepthTexture == null)
        {
            _cameraDepthTexture = Shader.GetGlobalTexture("_CameraDepthTexture");
            _grassComputeShader.SetTextureFromGlobal(_kernelChunkRender, "_DepthTexture", "_CameraDepthTexture");
            return;
        }

        _grassComputeShader.SetFloat("depthBias", _depthBias);
        RenderInstances();
    }

    private void OnDestroy()
    {
        _argsBuffer?.Release();
        _argsBuffer?.Dispose();
        _trsBuffer?.Release();
        _trsBuffer?.Dispose();
        _visibleBuffer?.Release();
        _visibleBuffer?.Dispose();
        _readBackArgsBuffer?.Release();
        _readBackArgsBuffer?.Dispose();
        _chunkBuffer?.Release();
        _chunkBuffer?.Dispose();
        _instanceCounterBuffer?.Release();
        _instanceCounterBuffer?.Dispose();
    }

    private void OnDrawGizmos()
    {
        if (!_drawGizmos) return;
        Gizmos.color = Color.red;
        Gizmos.DrawWireCube(transform.position, new Vector3(_range.x * 2, 5, _range.y * 2));
    }


    private void RenderInstances()
    {
        UpdateCameraViewProjectionMatrix();

        _material.SetFloat("_RecieveShadow", _recieveShadows ? 1f : 0f);

        if (_mesh == null) return;

        _grassComputeShader.SetVector("camPos", _cam.transform.position);
        _grassComputeShader.SetFloat("maxViewDistance", _maxViewDistance);
        _grassComputeShader.SetVectorArray("viewFrustumPlanes", GetViewFrustumPlaneNormals());

        _visibleBuffer.SetCounterValue(0);
        _grassComputeShader.Dispatch(_kernelChunkRender, Mathf.FloorToInt(_numChunks / _threadsChunkRender), 1, 1);

        SetVisibleInstanceCount();

        _argsBuffer.SetData(new uint[5] {
            _mesh.GetIndexCount(0), (uint)_visibleInstanceCount, 0, 0, 0
        });

        Graphics.DrawMeshInstancedIndirect(
          _mesh,
          0,
          _material,
          _renderBounds,
          _argsBuffer,
          0,
          null,
          _castShadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off
        );
    }

    /// <summary>
    /// Updates vpMatrix property in compute shader. Forced to make our own VP matrix since UNITY_MATRIX_VP does not contain the right values for us.
    /// </summary>
    private void UpdateCameraViewProjectionMatrix()
    {
        Matrix4x4 mat = GL.GetGPUProjectionMatrix(_cam.projectionMatrix, false) * _cam.worldToCameraMatrix;
        _grassComputeShader.SetMatrix("vpMatrix", mat);
    }

    /// <summary>
    /// Fetches amount of elements inside _visibleBuffer with an additional buffer.
    /// Stores result in _visibleInstanceCount.
    /// </summary>
    private void SetVisibleInstanceCount()
    {
        ComputeBuffer.CopyCount(_visibleBuffer, _readBackArgsBuffer, 0);
        int[] appendBufferCount = new int[1];
        _readBackArgsBuffer.GetData(appendBufferCount);
        _visibleInstanceCount = appendBufferCount[0];
    }

    /// <summary>
    /// Initializes grass chunk array with their x and z positions in a grid.
    /// </summary>
    private void InitializeGrassChunkPositions()
    {

        //whole range from -range.x to range.x
        int wholeRangeX = (int)(_range.x * 2);
        int wholeRangeZ = (int)(_range.y * 2);

        _numChunks = Mathf.CeilToInt(wholeRangeX / _chunkSize) * Mathf.CeilToInt(wholeRangeZ / _chunkSize);
        _chunks = new Chunk[_numChunks];

        //Used for centering grid
        int chunkSizeHalf = Mathf.CeilToInt(_chunkSize / 2);

        int startOffsetX = Mathf.CeilToInt(_range.x - chunkSizeHalf);
        int startOffsetZ = Mathf.CeilToInt(_range.y - chunkSizeHalf);
        Vector3 gridStartPos = transform.position - new Vector3(startOffsetX, 0, startOffsetZ);

        int xOffset = 0;
        int zOffset = 0;

        int chunksPerRow = wholeRangeX / _chunkSize;

        for (int i = 0; i < _numChunks; i++)
        {
            Vector3 p = gridStartPos;
            p.x += _chunkSize * xOffset;
            p.z += _chunkSize * zOffset;

            bool isSameRow = (i % chunksPerRow) < chunksPerRow - 1;

            xOffset = isSameRow ? xOffset + 1 : 0; //if same row then continue forward, else start at 0 again
            zOffset = isSameRow ? zOffset : zOffset + 1; //if same row keep z else got to the right (next row)

            _chunks[i] = new Chunk(p);
        }
    }

    /// <summary>
    /// Initializes grass instances for each chunk via compute shader
    /// </summary>
    private void InitializeGrassChunkInstances()
    {
        int instancesPerChunk = Mathf.CeilToInt(_instances / _numChunks);
        print("Num chunks : " + _numChunks);
        print("instances per chunk : " + instancesPerChunk);

        _kernelChunkRender = _grassComputeShader.FindKernel("ChunkRender");
        _kernelInitChunkInstanceCount = _grassComputeShader.FindKernel("InitChunkInstanceCount");
        _kernelInitInstanceTransforms = _grassComputeShader.FindKernel("InitInstanceTransforms");

        _grassComputeShader.SetFloat("grassThreshhold", _grassThreshhold);

        _grassComputeShader.SetInt("numChunks", _numChunks);
        _grassComputeShader.SetInt("chunkSize", _chunkSize);
        _grassComputeShader.SetInt("instancesPerChunk", instancesPerChunk);
        _grassComputeShader.SetFloat("depthBias", _depthBias);

        _grassComputeShader.SetVectorArray("viewFrustumPlanes", GetViewFrustumPlaneNormals());

        _grassComputeShader.SetVector("scaleMin", _scaleMin);
        _grassComputeShader.SetVector("scaleMin", _scaleMax);
        _grassComputeShader.SetFloat("scaleNoiseScale", _scaleNoiseScale);

        _grassComputeShader.SetVector("terrainSize", _terrainData.size);
        _grassComputeShader.SetVector("terrainPos", _terrain.transform.position);
        _grassComputeShader.SetInt("terrainHeightmapResolution", _terrainData.heightmapResolution);

        _grassComputeShader.SetTexture(_kernelInitInstanceTransforms, "Heightmap", _terrainData.heightmapTexture);
        _grassComputeShader.SetTexture(_kernelInitInstanceTransforms, "Splatmap", _terrainData.alphamapTextures[0]);

        _grassComputeShader.SetTexture(_kernelInitChunkInstanceCount, "Heightmap", _terrainData.heightmapTexture);
        _grassComputeShader.SetTexture(_kernelInitChunkInstanceCount, "Splatmap", _terrainData.alphamapTextures[0]);


        _argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
        _readBackArgsBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);

        _instanceCounterBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);
        _instanceCounterBuffer.SetData(new int[] { 0 });

        _chunkBuffer = new ComputeBuffer(_numChunks, 3 * sizeof(float) + 2 * sizeof(int));
        _chunkBuffer.SetData(_chunks);

        _grassComputeShader.SetBuffer(_kernelInitChunkInstanceCount, "chunkBuffer", _chunkBuffer);
        _grassComputeShader.SetBuffer(_kernelInitChunkInstanceCount, "instanceCounter", _instanceCounterBuffer);

        _grassComputeShader.SetBuffer(_kernelInitInstanceTransforms, "chunkBuffer", _chunkBuffer);
        _grassComputeShader.SetBuffer(_kernelInitInstanceTransforms, "instanceCounter", _instanceCounterBuffer);

        // First calculate instances per chunk
        _grassComputeShader.Dispatch(_kernelInitChunkInstanceCount, Mathf.CeilToInt(_numChunks / _threadsChunkInit), 1, 1);

        // _instanceCounterBuffer now contains sum of all instances of all chunk
        int[] cb = new int[1];
        _instanceCounterBuffer.GetData(cb);
        _trueInstanceCount = cb[0];

        // Now generate the buffer for rendering
        _trsBuffer = new ComputeBuffer(_trueInstanceCount, sizeof(float) * 4 * 4);
        _grassComputeShader.SetBuffer(_kernelInitInstanceTransforms, "trsBuffer", _trsBuffer);

        // Fill buffer for rendering with chunk data
        _grassComputeShader.Dispatch(_kernelInitInstanceTransforms, Mathf.CeilToInt(_numChunks / _threadsChunkInit), 1, 1);

        _visibleBuffer = new ComputeBuffer(_trueInstanceCount, sizeof(float) * 4 * 4, ComputeBufferType.Append);
        _grassComputeShader.SetBuffer(_kernelChunkRender, "visibleList", _visibleBuffer);
        _material.SetBuffer("visibleList", _visibleBuffer);

        _grassComputeShader.SetBuffer(_kernelChunkRender, "trsBuffer", _trsBuffer);
        _grassComputeShader.SetBuffer(_kernelChunkRender, "chunkBuffer", _chunkBuffer);

        _instanceCounterBuffer?.Release();

    }

    private Vector4[] GetViewFrustumPlaneNormals()
    {
        Vector4[] planeNormals = new Vector4[6];
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(_cam);

        for (int i = 0; i < 6; i++)
        {
            planeNormals[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);
        }
        return planeNormals;
    }

}
