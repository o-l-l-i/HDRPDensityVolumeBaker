using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using System;
using System.IO;
using UnityEngine.Rendering.HighDefinition;


// Density Volume texture baker prototype by Olli Sorjonen.

// Compute shader uses Ashima Arts noise library translated and modified by Keijiro Takahashi.
// (source: https://github.com/keijiro/NoiseShader).

// A few notes:

// Density Volume requires a Texture3D with resolution of 32x32x32, in Alpha8 format.
// It does not accept a RenderTexture, which makes things a bit complicated.
// 3D RenderTexture needs to be deconstructed to 2D slices, which are then reconstructed into a Texture3D

// See Unity HDRP documentation for information about Density Volumes.

public class VolumeBaker : MonoBehaviour
{
    [Header("Compute Program for Volume Rendering")]
    [Tooltip("Assign the compute shader used to render the volume data.")]
    [SerializeField] ComputeShader computeShader = null;

    [Header("Density Volume Object")]
    [Tooltip("Assign a scene object with a Density Volume applied to it.")]
    [SerializeField] DensityVolume densityVolume;

    int voxelResolution = 32;

    enum Shape
    {
        Sphere,
        Cylinder,
        Torus,
        Noise
    };

    [Header("Shape Settings")]
    [Tooltip("Shape to render, a few choices")]
    [SerializeField] Shape shape;
    [Header("Wrong values won't produce correct results. Use something around 10-15.")]
    [SerializeField] float fallOff = 10f;

    [Header("Noise Settings")]
    [SerializeField] bool enableNoise = false;
    [Tooltip("Noise pattern density.")]
    [Range(1, 10)] [SerializeField] float noiseDensity = 5f;
    [Range(1, 10)] [SerializeField] float noiseIntensity = 5f;

    [Header("Saving")]
    [Tooltip("File name for the saved Texture3D asset. Save location is in project's Assets folder.")]
    [SerializeField] string saveFileName = "densityMaskTexture";
    [Tooltip("Existing files will not be overwritten in project's Assets folder.")]
    [SerializeField] bool saveToDisk = false;

    int kernel_render, kernel_slice;

    static class ShaderIDs
    {
        internal static readonly int VoxelRT = Shader.PropertyToID("_VoxelTexture");
        internal static readonly int SliceRT = Shader.PropertyToID("_SliceTexture");
        internal static readonly int VoxelResolution = Shader.PropertyToID("_VoxelResolution");
        internal static readonly int FallOff = Shader.PropertyToID("_FallOff");
        internal static readonly int Slice = Shader.PropertyToID("_Slice");
        internal static readonly int NoiseDensity = Shader.PropertyToID("_NoiseDensity");
        internal static readonly int NoiseIntensity = Shader.PropertyToID("_NoiseIntensity");
        internal static readonly int EnableNoise = Shader.PropertyToID("_EnableNoise");
        internal static readonly int DrawSphere = Shader.PropertyToID("_DrawSphere");
        internal static readonly int DrawCylinder = Shader.PropertyToID("_DrawCylinder");
        internal static readonly int DrawTorus = Shader.PropertyToID("_DrawTorus");
        internal static readonly int DrawNoise = Shader.PropertyToID("_DrawNoise");
    }


    void OnValidate()
    {
        if (String.IsNullOrEmpty(saveFileName))
        {
            if (densityVolume != null)
                saveFileName = densityVolume.name + "_densityTex";
            else
                Debug.Log("<color=red>Save file name must not be empty!</color>");
        }
    }


    void Start()
    {
        kernel_render = computeShader.FindKernel("GPU_RenderVolume");
        kernel_slice = computeShader.FindKernel("GPU_Slice");

        RenderTexture voxelRT = InitVoxelRenderTexture3D();

        voxelRT = GenerateSDF(voxelRT);

        Texture3D result = RenderTexture3D(voxelRT);

        densityVolume.parameters.volumeMask = result;

        if (saveToDisk)
            Save(result);
    }


    RenderTexture InitVoxelRenderTexture3D()
    {
        RenderTexture voxelRT = new RenderTexture(voxelResolution, voxelResolution, 0, RenderTextureFormat.ARGBHalf);
        voxelRT.volumeDepth = voxelResolution;
        voxelRT.dimension = TextureDimension.Tex3D;
        voxelRT.wrapMode = TextureWrapMode.Clamp;
        voxelRT.filterMode = FilterMode.Bilinear;
        voxelRT.useMipMap = false;
        voxelRT.enableRandomWrite = true;
        voxelRT.name = densityVolume.name + "_RT";
        voxelRT.Create();

        return voxelRT;
    }


    RenderTexture GenerateSDF(RenderTexture voxelRT)
    {
        computeShader.SetTexture(kernel_render, ShaderIDs.VoxelRT, voxelRT);

        computeShader.SetInt(ShaderIDs.VoxelResolution, voxelResolution);
        computeShader.SetFloat(ShaderIDs.FallOff, fallOff);
        computeShader.SetBool(ShaderIDs.EnableNoise, enableNoise);
        computeShader.SetFloat(ShaderIDs.NoiseDensity, noiseDensity);
        computeShader.SetFloat(ShaderIDs.NoiseIntensity, noiseIntensity);
        if (shape == Shape.Sphere)
        {
            computeShader.SetBool(ShaderIDs.DrawSphere, true);
            computeShader.SetBool(ShaderIDs.DrawCylinder, false);
            computeShader.SetBool(ShaderIDs.DrawTorus, false);
            computeShader.SetBool(ShaderIDs.DrawNoise, false);
        }
        if (shape == Shape.Cylinder)
        {
            computeShader.SetBool(ShaderIDs.DrawSphere, false);
            computeShader.SetBool(ShaderIDs.DrawCylinder, true);
            computeShader.SetBool(ShaderIDs.DrawTorus, false);
            computeShader.SetBool(ShaderIDs.DrawNoise, false);
        }
        if (shape == Shape.Torus)
        {
            computeShader.SetBool(ShaderIDs.DrawSphere, false);
            computeShader.SetBool(ShaderIDs.DrawCylinder, false);
            computeShader.SetBool(ShaderIDs.DrawTorus, true);
            computeShader.SetBool(ShaderIDs.DrawNoise, false);
        }
        if (shape == Shape.Noise)
        {
            computeShader.SetBool(ShaderIDs.DrawSphere, false);
            computeShader.SetBool(ShaderIDs.DrawCylinder, false);
            computeShader.SetBool(ShaderIDs.DrawTorus, false);
            computeShader.SetBool(ShaderIDs.DrawNoise, true);
        }

        int dim = Mathf.Max(8, voxelRT.width / 8);
        computeShader.Dispatch(kernel_render, dim, dim, dim);

        return voxelRT;
    }


    RenderTexture Copy3DSliceToRenderTexture(RenderTexture voxelRT, int currentSlice)
    {
        RenderTexture slice = new RenderTexture(voxelResolution, voxelResolution, 0, RenderTextureFormat.RFloat);
        slice.dimension = UnityEngine.Rendering.TextureDimension.Tex2D;
        slice.wrapMode = TextureWrapMode.Clamp;
        slice.enableRandomWrite = true;
        slice.Create();

        computeShader.SetTexture(kernel_slice, ShaderIDs.VoxelRT, voxelRT );
        computeShader.SetTexture(kernel_slice, ShaderIDs.SliceRT, slice);

        computeShader.SetInt(ShaderIDs.Slice, currentSlice);

        int dim = Mathf.Max(32, voxelRT.width / 32);
        computeShader.Dispatch(kernel_slice, dim, dim, 1);

        return slice;
    }


    Texture2D ConvertRTtoTexture2D(RenderTexture renderTex)
    {
        Texture2D output = new Texture2D(voxelResolution, voxelResolution);
        RenderTexture.active = renderTex;
        output.ReadPixels(new Rect(0, 0, voxelResolution, voxelResolution), 0, 0);
        output.Apply();

        return output;
    }


    Texture3D RenderTexture3D(RenderTexture voxelRT)
    {
        Texture2D[] volumeSlices = new Texture2D[voxelResolution];
        for ( int i = 0; i < voxelResolution; i++)
        {
            RenderTexture sliceRT = Copy3DSliceToRenderTexture(voxelRT, i);
            volumeSlices[i] = ConvertRTtoTexture2D(sliceRT);
        }

        Texture3D outputTexture = new Texture3D(voxelResolution, voxelResolution, voxelResolution, TextureFormat.Alpha8, false);
        outputTexture.wrapMode = TextureWrapMode.Clamp;
        outputTexture.filterMode = FilterMode.Bilinear;

        outputTexture.name = densityVolume.name + "_densityTex";
        Color[] outputPixels = new Color[voxelResolution * voxelResolution * voxelResolution];

        for (int z = 0; z < voxelResolution; z++)
        {
            Color[] slicePixels = volumeSlices[z].GetPixels();

            for (int y = 0; y < voxelResolution; y++)
            {
                for (int x = 0; x < voxelResolution; x++)
                {
                    Color col = slicePixels[y + x * voxelResolution];
                    float a = (col.r + col.g + col.b) / 3f;
                    Color c = new Color (a,a,a,a);
                    outputPixels[x + (y * voxelResolution) + (z * voxelResolution * voxelResolution)] = c;
                }
            }
        }

        outputTexture.SetPixels(outputPixels);
        outputTexture.Apply();

        return outputTexture;
    }


    void Save(Texture3D saveTexture)
    {
        string path = Application.dataPath + "/" + saveFileName + ".asset";

        if (File.Exists(path)) {
            Debug.Log("<color=red>File already exits at " + path + " - not overwriting.</color>");
        }
        else {
            Debug.Log("Saved density volume to: " + path);
            AssetDatabase.CreateAsset(saveTexture, "Assets/" + saveFileName + ".asset");
        }
    }

}