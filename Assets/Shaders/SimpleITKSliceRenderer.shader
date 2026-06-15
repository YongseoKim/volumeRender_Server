Shader "SimpleITK/SliceRenderer"
{
    Properties
    {
        _VolumeTex("Volume Texture", 3D) = "" {}
        _TransferTex("Transfer Function (1D)", 2D) = "white" {}
        _TransferTex2D("Transfer Function (2D)", 2D) = "black" {}
        _SegmentationTex("Segmentation Texture", 3D) = "" {}
        _SegmentationColorTex("Segmentation Colormap", 2D) = "black" {}
        _UseTF2D("Use 2D TF", Float) = 0
        _UseSegmentationOverlay("Use Segmentation Overlay", Float) = 0
        _SegmentationOverlayOpacity("Segmentation Overlay Opacity", Range(0, 1)) = 0.45
        _ClipMin("Clip Min", Range(0, 1)) = 0
        _ClipMax("Clip Max", Range(0, 1)) = 1
        _EnableRoi("Enable ROI", Float) = 0
        _RoiMin("ROI Min", Vector) = (0, 0, 0, 0)
        _RoiMax("ROI Max", Vector) = (1, 1, 1, 0)
        _RoiKeepInside("ROI Keep Inside", Float) = 1
        _GradientScale("Gradient Scale", Float) = 2.0
        _InvVolumeSize("Inverse Volume Size", Vector) = (0.01, 0.01, 0.01, 0)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 worldPos : TEXCOORD0;
            };

            TEXTURE3D(_VolumeTex);
            SAMPLER(sampler_VolumeTex);

            TEXTURE2D(_TransferTex);
            SAMPLER(sampler_TransferTex);

            TEXTURE2D(_TransferTex2D);
            SAMPLER(sampler_TransferTex2D);

            TEXTURE3D(_SegmentationTex);
            SAMPLER(sampler_SegmentationTex);

            TEXTURE2D(_SegmentationColorTex);
            SAMPLER(sampler_SegmentationColorTex);

            CBUFFER_START(UnityPerMaterial)
                float4x4 _WorldToVolume;
                float _UseTF2D;
                float _UseSegmentationOverlay;
                float _SegmentationOverlayOpacity;
                float _ClipMin;
                float _ClipMax;
                float _EnableRoi;
                float4 _RoiMin;
                float4 _RoiMax;
                float _RoiKeepInside;
                float _GradientScale;
                float4 _InvVolumeSize;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionHCS = positionInputs.positionCS;
                output.worldPos = positionInputs.positionWS;
                return output;
            }

            float ComputeGradientMagnitude(float3 samplePos)
            {
                float3 delta = max(_InvVolumeSize.xyz, float3(1e-5, 1e-5, 1e-5));

                float sx1 = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, samplePos + float3(delta.x, 0.0, 0.0)).r;
                float sx0 = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, samplePos - float3(delta.x, 0.0, 0.0)).r;
                float sy1 = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, samplePos + float3(0.0, delta.y, 0.0)).r;
                float sy0 = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, samplePos - float3(0.0, delta.y, 0.0)).r;
                float sz1 = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, samplePos + float3(0.0, 0.0, delta.z)).r;
                float sz0 = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, samplePos - float3(0.0, 0.0, delta.z)).r;

                float3 gradient = 0.5 * float3(sx1 - sx0, sy1 - sy0, sz1 - sz0);
                return saturate(length(gradient) * _GradientScale);
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 volumePos = mul(_WorldToVolume, float4(input.worldPos, 1.0)).xyz + 0.5;
                if (any(volumePos < 0.0) || any(volumePos > 1.0))
                {
                    discard;
                }

                if (_EnableRoi > 0.5)
                {
                    bool insideRoi =
                        volumePos.x >= _RoiMin.x && volumePos.x <= _RoiMax.x &&
                        volumePos.y >= _RoiMin.y && volumePos.y <= _RoiMax.y &&
                        volumePos.z >= _RoiMin.z && volumePos.z <= _RoiMax.z;

                    bool clipped = _RoiKeepInside > 0.5 ? !insideRoi : insideRoi;
                    if (clipped)
                    {
                        discard;
                    }
                }

                float density = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, volumePos).r;
                float clipRange = max(_ClipMax - _ClipMin, 1e-5);
                float densityWindowed = saturate((density - _ClipMin) / clipRange);

                float4 colour;
                if (_UseTF2D > 0.5)
                {
                    float gradMag = ComputeGradientMagnitude(volumePos);
                    colour = SAMPLE_TEXTURE2D(_TransferTex2D, sampler_TransferTex2D, float2(densityWindowed, gradMag));
                }
                else
                {
                    colour = SAMPLE_TEXTURE2D(_TransferTex, sampler_TransferTex, float2(densityWindowed, 0.5));
                }

                // Fallback: if TF texture is empty/uninitialized, use grayscale density.
                if (dot(colour.rgb, float3(0.3333, 0.3333, 0.3333)) <= 1e-4)
                {
                    colour.rgb = densityWindowed.xxx;
                }

                if (_UseSegmentationOverlay > 0.5)
                {
                    float labelNormalized = SAMPLE_TEXTURE3D(_SegmentationTex, sampler_SegmentationTex, volumePos).r;
                    float label = round(labelNormalized * 255.0);
                    if (label > 0.5)
                    {
                        float paletteU = (label + 0.5) / 256.0;
                        float4 overlay = SAMPLE_TEXTURE2D(_SegmentationColorTex, sampler_SegmentationColorTex, float2(paletteU, 0.5));
                        float overlayAlpha = saturate(overlay.a * _SegmentationOverlayOpacity);
                        colour.rgb = lerp(colour.rgb, overlay.rgb, overlayAlpha);
                    }
                }

                colour.a = 1.0;
                return half4(colour);
            }
            ENDHLSL
        }
    }
}
