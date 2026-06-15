Shader "SimpleITK/CinematicVolumeRaymarch"
{
    Properties
    {
        _VolumeTex("Volume Texture", 3D) = "" {}
        _TransferTex("Transfer Function (1D)", 2D) = "white" {}
        _TransferTex2D("Transfer Function (2D)", 2D) = "black" {}
        _SegmentationTex("Segmentation Texture", 3D) = "" {}
        _SegmentationColorTex("Segmentation Colormap", 2D) = "black" {}
        _StepSize("Step Size", Float) = 0.003
        _MaxSteps("Max Steps", Float) = 256
        _Opacity("Opacity", Float) = 1.0
        _Brightness("Brightness", Float) = 1.0
        _UseSegmentationOverlay("Use Segmentation Overlay", Float) = 0
        _SegmentationOverlayOpacity("Segmentation Overlay Opacity", Range(0, 1)) = 0.35
        _ClipMin("Clip Min", Range(0, 1)) = 0
        _ClipMax("Clip Max", Range(0, 1)) = 1
        _TFMode("TF Mode (0=1D,1=2D)", Float) = 0
        _GradientScale("Gradient Scale", Float) = 2.0
        _InvVolumeSize("Inverse Volume Size", Vector) = (0.01, 0.01, 0.01, 0)

        _EnableBoxCut("Enable Box Cut", Float) = 0
        _BoxMin("Box Min", Vector) = (0.3, 0.3, 0.3, 0)
        _BoxMax("Box Max", Vector) = (0.7, 0.7, 0.7, 0)
        _BoxInvert("Invert Box", Float) = 0

        _LightingMode("Lighting Mode", Float) = 0
        _LightDirOS("Light Direction (Object Space)", Vector) = (0, -1, 0, 0)

        _IsoThreshold("Isosurface Threshold", Range(0, 1)) = 0.35
        _IsoColor("Isosurface Color", Color) = (1, 1, 1, 1)
        _IsoAmbient("Isosurface Ambient", Float) = 0.15
        _IsoDiffuse("Isosurface Diffuse", Float) = 1.1
        _IsoSpecular("Isosurface Specular", Float) = 0.35
        _IsoSpecPower("Isosurface Specular Power", Float) = 32

        _CinematicLightFollowsCamera("Cinematic Light Follows Camera", Float) = 1
        _CinematicAmbient("Cinematic Ambient", Float) = 0.2
        _CinematicLightIntensity("Cinematic Light Intensity", Float) = 1.0
        _CinematicDiffuse("Cinematic Diffuse", Float) = 0.7
        _CinematicSpecular("Cinematic Specular", Float) = 0.3
        _CinematicSpecPower("Cinematic Specular Power", Float) = 24
        _CinematicShadowStrength("Cinematic Shadow Strength", Float) = 0.8
        _CinematicShadowStepMultiplier("Cinematic Shadow Step Multiplier", Float) = 1.0
        _CinematicShadowMaxSteps("Cinematic Shadow Max Steps", Float) = 48
        _CinematicScatterBlend("Cinematic Scatter Blend", Float) = 0.5
        _CinematicExtinction("Cinematic Extinction", Float) = 1.25
        _CinematicAoStrength("Cinematic AO Strength", Float) = 0.85
        _CinematicAoRadius("Cinematic AO Radius", Float) = 2.5
        _CinematicAoSamples("Cinematic AO Samples", Float) = 4
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
        ZWrite Off
        Cull Off

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _StepSize;
                float _MaxSteps;
                float _Opacity;
                float _Brightness;
                float _UseSegmentationOverlay;
                float _SegmentationOverlayOpacity;
                float _ClipMin;
                float _ClipMax;
                float _TFMode;
                float _GradientScale;
                float4 _InvVolumeSize;

                float _EnableBoxCut;
                float4 _BoxMin;
                float4 _BoxMax;
                float _BoxInvert;

                float _LightingMode;
                float4 _LightDirOS;

                float _IsoThreshold;
                float4 _IsoColor;
                float _IsoAmbient;
                float _IsoDiffuse;
                float _IsoSpecular;
                float _IsoSpecPower;

                float _CinematicLightFollowsCamera;
                float _CinematicAmbient;
                float _CinematicLightIntensity;
                float _CinematicDiffuse;
                float _CinematicSpecular;
                float _CinematicSpecPower;
                float _CinematicShadowStrength;
                float _CinematicShadowStepMultiplier;
                float _CinematicShadowMaxSteps;
                float _CinematicScatterBlend;
                float _CinematicExtinction;
                float _CinematicAoStrength;
                float _CinematicAoRadius;
                float _CinematicAoSamples;
            CBUFFER_END

            #include "SimpleITKVolumeRaymarchCommon.hlsl"

            half4 frag(Varyings input) : SV_Target
            {
                float3 rayOrigin;
                float3 rayDir;

                if (unity_OrthoParams.w > 0.5)
                {
                    float3 camForwardWS = -float3(UNITY_MATRIX_V[0][2], UNITY_MATRIX_V[1][2], UNITY_MATRIX_V[2][2]);
                    rayDir = normalize(TransformWorldToObjectDir(camForwardWS));
                    rayOrigin = input.positionOS01;
                }
                else
                {
                    rayOrigin = TransformWorldToObject(GetCameraPositionWS()) + 0.5;
                    rayDir = normalize(input.positionOS01 - rayOrigin);
                }

                float2 hit = IntersectUnitBox(rayOrigin, rayDir);
                float tNear = max(hit.x, 0.0);
                float tFar = hit.y;

                if (tFar <= tNear)
                {
                    discard;
                }

                float stepSize = max(_StepSize, 1e-5);
                float clipRange = max(_ClipMax - _ClipMin, 1e-5);
                int maxSteps = clamp((int)_MaxSteps, 1, 2048);

                if (_LightingMode > 0.5 && _LightingMode < 1.5)
                {
                    float threshold = saturate(_IsoThreshold);
                    float3 viewDir = normalize(-rayDir);
                    float3 prevPos = rayOrigin + rayDir * tNear;
                    float prevWindowed = 0.0;
                    bool prevValid = IsCutout(prevPos) <= 0.5;
                    if (prevValid)
                    {
                        float prevDensity = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, prevPos).r;
                        prevWindowed = saturate((prevDensity - _ClipMin) / clipRange);
                        if (prevWindowed >= threshold)
                        {
                            float4 overlay0 = EvaluateSegmentationOverlay(prevPos);
                            float3 isoColor0 = lerp(_IsoColor.rgb, overlay0.rgb, overlay0.a);
                            float3 isoLit0 = ShadeIsosurface(prevPos, viewDir, isoColor0) * _Brightness;
                            return half4(isoLit0, 1.0);
                        }
                    }

                    float tIso = tNear + stepSize;
                    [loop]
                    for (int i = 0; i < 2048; ++i)
                    {
                        if (i >= maxSteps || tIso > tFar)
                        {
                            break;
                        }

                        float3 samplePos = rayOrigin + rayDir * tIso;
                        if (IsCutout(samplePos) > 0.5)
                        {
                            prevValid = false;
                            tIso += stepSize;
                            continue;
                        }

                        float density = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, samplePos).r;
                        float densityWindowed = saturate((density - _ClipMin) / clipRange);
                        if (densityWindowed >= threshold)
                        {
                            float3 isoPos = samplePos;
                            if (prevValid && prevWindowed < threshold)
                            {
                                float denom = max(densityWindowed - prevWindowed, 1e-5);
                                float lerpFactor = saturate((threshold - prevWindowed) / denom);
                                isoPos = lerp(prevPos, samplePos, lerpFactor);
                            }

                            float4 overlay = EvaluateSegmentationOverlay(isoPos);
                            float3 isoColor = lerp(_IsoColor.rgb, overlay.rgb, overlay.a);
                            float3 isoLit = ShadeIsosurface(isoPos, viewDir, isoColor) * _Brightness;
                            return half4(isoLit, 1.0);
                        }

                        prevPos = samplePos;
                        prevWindowed = densityWindowed;
                        prevValid = true;
                        tIso += stepSize;
                    }

                    discard;
                }

                float4 accum = float4(0.0, 0.0, 0.0, 0.0);
                float3 viewDir = normalize(-rayDir);
                float t = tNear;

                [loop]
                for (int i = 0; i < 2048; ++i)
                {
                    if (i >= maxSteps || t > tFar || accum.a >= 0.995)
                    {
                        break;
                    }

                    float3 samplePos = rayOrigin + rayDir * t;
                    if (IsCutout(samplePos) > 0.5)
                    {
                        t += stepSize;
                        continue;
                    }

                    float density = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, samplePos).r;
                    float4 tf = EvaluateTransferFunction(samplePos, density, clipRange);
                    float4 overlay = EvaluateSegmentationOverlay(samplePos);
                    if (overlay.a > 0.0)
                    {
                        tf.rgb = lerp(tf.rgb, overlay.rgb, overlay.a);
                        tf.a = max(tf.a, overlay.a);
                    }

                    float alpha = saturate(tf.a * _Opacity);
                    float3 lit = tf.rgb;
                    if (_LightingMode > 1.5 && alpha > 0.005)
                    {
                        lit = ShadeCinematicVolume(samplePos, viewDir, lit, clipRange, _LightingMode);
                    }

                    lit *= _Brightness;
                    accum.rgb += (1.0 - accum.a) * alpha * lit;
                    accum.a += (1.0 - accum.a) * alpha;

                    t += stepSize;
                }

                return half4(accum);
            }
            ENDHLSL
        }
    }
}
