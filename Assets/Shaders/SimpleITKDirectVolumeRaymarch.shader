Shader "SimpleITK/DirectVolumeRaymarch"
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

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionOS01 : TEXCOORD0;
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
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionHCS = positionInputs.positionCS;
                output.positionOS01 = input.positionOS.xyz + 0.5;
                return output;
            }

            float2 IntersectUnitBox(float3 rayOrigin, float3 rayDirection)
            {
                float3 dirSign = lerp(float3(-1.0, -1.0, -1.0), float3(1.0, 1.0, 1.0), step(0.0, rayDirection));
                float3 dirSafe = dirSign * max(abs(rayDirection), float3(1e-5, 1e-5, 1e-5));

                float3 t0 = (float3(0.0, 0.0, 0.0) - rayOrigin) / dirSafe;
                float3 t1 = (float3(1.0, 1.0, 1.0) - rayOrigin) / dirSafe;

                float3 tMin = min(t0, t1);
                float3 tMax = max(t0, t1);

                float tNear = max(max(tMin.x, tMin.y), tMin.z);
                float tFar = min(min(tMax.x, tMax.y), tMax.z);
                return float2(tNear, tFar);
            }

            float IsBoxCutout(float3 samplePos)
            {
                if (_EnableBoxCut < 0.5)
                {
                    return 0.0;
                }

                float inside =
                    step(_BoxMin.x, samplePos.x) * step(samplePos.x, _BoxMax.x) *
                    step(_BoxMin.y, samplePos.y) * step(samplePos.y, _BoxMax.y) *
                    step(_BoxMin.z, samplePos.z) * step(samplePos.z, _BoxMax.z);

                float cut = inside;
                return _BoxInvert > 0.5 ? (1.0 - cut) : cut;
            }

            float IsCutout(float3 samplePos)
            {
                return saturate(IsBoxCutout(samplePos));
            }

            float3 ComputeGradient(float3 samplePos)
            {
                float3 d = max(_InvVolumeSize.xyz, float3(1e-5, 1e-5, 1e-5));
                float sx1 = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, samplePos + float3(d.x, 0.0, 0.0)).r;
                float sx0 = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, samplePos - float3(d.x, 0.0, 0.0)).r;
                float sy1 = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, samplePos + float3(0.0, d.y, 0.0)).r;
                float sy0 = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, samplePos - float3(0.0, d.y, 0.0)).r;
                float sz1 = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, samplePos + float3(0.0, 0.0, d.z)).r;
                float sz0 = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, samplePos - float3(0.0, 0.0, d.z)).r;
                return 0.5 * float3(sx1 - sx0, sy1 - sy0, sz1 - sz0);
            }

            float ComputeGradientMagnitude(float3 samplePos)
            {
                return saturate(length(ComputeGradient(samplePos)) * _GradientScale);
            }

            float4 EvaluateTransferFunction(float3 samplePos, float density, float clipRange)
            {
                float densityWindowed = saturate((density - _ClipMin) / clipRange);
                float4 tf;
                if (_TFMode > 0.5)
                {
                    float grad = ComputeGradientMagnitude(samplePos);
                    tf = SAMPLE_TEXTURE2D(_TransferTex2D, sampler_TransferTex2D, float2(densityWindowed, grad));
                }
                else
                {
                    tf = SAMPLE_TEXTURE2D(_TransferTex, sampler_TransferTex, float2(densityWindowed, 0.5));
                }

                // Fallback: if TF texture is empty/uninitialized, fall back to grayscale density mapping
                // so runtime never collapses to fully black/transparent.
                float rgbEnergy = dot(tf.rgb, float3(0.3333, 0.3333, 0.3333));
                if (rgbEnergy <= 1e-4 && tf.a <= 1e-4)
                {
                    tf = float4(densityWindowed.xxx, densityWindowed);
                }
                else if (tf.a <= 1e-4)
                {
                    tf.a = densityWindowed;
                }

                return tf;
            }

            float4 EvaluateSegmentationOverlay(float3 samplePos)
            {
                if (_UseSegmentationOverlay <= 0.5)
                {
                    return float4(0.0, 0.0, 0.0, 0.0);
                }

                float labelNormalized = SAMPLE_TEXTURE3D(_SegmentationTex, sampler_SegmentationTex, samplePos).r;
                float label = round(labelNormalized * 255.0);
                if (label <= 0.5)
                {
                    return float4(0.0, 0.0, 0.0, 0.0);
                }

                float paletteU = (label + 0.5) / 256.0;
                float4 overlay = SAMPLE_TEXTURE2D(_SegmentationColorTex, sampler_SegmentationColorTex, float2(paletteU, 0.5));
                overlay.a = saturate(overlay.a * _SegmentationOverlayOpacity);
                return overlay;
            }

            float3 ShadeIsosurface(float3 samplePos, float3 viewDir, float3 baseColor)
            {
                float3 grad = ComputeGradient(samplePos);
                float gLen = length(grad);
                if (gLen <= 1e-5)
                {
                    return baseColor;
                }

                float3 normal = grad / gLen;
                if (dot(normal, viewDir) < 0.0)
                {
                    normal = -normal;
                }

                float3 lightDir = normalize(_LightDirOS.xyz);
                float ndotl = saturate(dot(normal, lightDir));
                float3 halfVector = normalize(lightDir + viewDir);
                float spec = pow(saturate(dot(normal, halfVector)), max(_IsoSpecPower, 1.0));

                float3 lit = baseColor * (_IsoAmbient + _IsoDiffuse * ndotl);
                lit += _IsoSpecular * spec;
                return lit;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 rayOrigin;
                float3 rayDir;

                if (unity_OrthoParams.w > 0.5)
                {
                    // Orthographic: every pixel shares the camera forward ray direction.
                    // Row 2 of UNITY_MATRIX_V corresponds to camera -Z in world space.
                    float3 camForwardWS = -float3(UNITY_MATRIX_V[0][2], UNITY_MATRIX_V[1][2], UNITY_MATRIX_V[2][2]);
                    rayDir = normalize(TransformWorldToObjectDir(camForwardWS));
                    // The fragment's object-space position is the entry point on the box surface.
                    rayOrigin = input.positionOS01;
                }
                else
                {
                    // Perspective: cast a ray from the camera position through each vertex.
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
                        bool cut = IsCutout(samplePos) > 0.5;
                        if (cut)
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
                    float3 lit = tf.rgb * _Brightness;

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
