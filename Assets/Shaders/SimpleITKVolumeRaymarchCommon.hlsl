#ifndef SIMPLEITK_VOLUME_RAYMARCH_COMMON_INCLUDED
#define SIMPLEITK_VOLUME_RAYMARCH_COMMON_INCLUDED

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

float3 SafeNormalize(float3 value, float3 fallback)
{
    float lengthSquared = dot(value, value);
    if (lengthSquared <= 1e-10)
    {
        return fallback;
    }

    return value * rsqrt(lengthSquared);
}

float GetAverageVoxelSize()
{
    float3 invSize = max(_InvVolumeSize.xyz, float3(1e-5, 1e-5, 1e-5));
    return (invSize.x + invSize.y + invSize.z) / 3.0;
}

bool IsOutsideUnitBox(float3 samplePos)
{
    return any(samplePos < float3(0.0, 0.0, 0.0)) || any(samplePos > float3(1.0, 1.0, 1.0));
}

bool TryGetGradientNormal(float3 samplePos, out float3 normal, out float gradientLength)
{
    float3 gradient = ComputeGradient(samplePos);
    gradientLength = length(gradient);
    if (gradientLength <= 1e-5)
    {
        normal = float3(0.0, 0.0, 1.0);
        return false;
    }

    normal = gradient / gradientLength;
    return true;
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

float EvaluateOpacityOnly(float3 samplePos, float clipRange)
{
    if (IsOutsideUnitBox(samplePos) || IsCutout(samplePos) > 0.5)
    {
        return 0.0;
    }

    float density = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, samplePos).r;
    float4 tf = EvaluateTransferFunction(samplePos, density, clipRange);
    return saturate(tf.a * _Opacity);
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

void BuildOrthonormalBasis(float3 normal, out float3 tangent, out float3 bitangent)
{
    float3 up = abs(normal.z) < 0.999 ? float3(0.0, 0.0, 1.0) : float3(0.0, 1.0, 0.0);
    tangent = SafeNormalize(cross(up, normal), float3(1.0, 0.0, 0.0));
    bitangent = SafeNormalize(cross(normal, tangent), float3(0.0, 1.0, 0.0));
}

float ComputeLocalAmbientOcclusion(float3 samplePos, float3 normal, float clipRange)
{
    int aoSamples = clamp((int)_CinematicAoSamples, 1, 8);
    float radius = max(_CinematicAoRadius * GetAverageVoxelSize(), GetAverageVoxelSize());
    float3 tangent;
    float3 bitangent;
    BuildOrthonormalBasis(normal, tangent, bitangent);

    float occlusion = 0.0;
    float weightSum = 0.0;

    [loop]
    for (int i = 0; i < 8; ++i)
    {
        if (i >= aoSamples)
        {
            break;
        }

        float fraction = (i + 1.0) / (float)aoSamples;
        float distance = radius * fraction;
        float weight = 1.0 - 0.55 * fraction;

        float sampleCenter = EvaluateOpacityOnly(samplePos + normal * distance, clipRange);
        float sampleTPlus = EvaluateOpacityOnly(samplePos + SafeNormalize(normal + tangent * 0.75, normal) * distance, clipRange);
        float sampleTMinus = EvaluateOpacityOnly(samplePos + SafeNormalize(normal - tangent * 0.75, normal) * distance, clipRange);
        float sampleBPlus = EvaluateOpacityOnly(samplePos + SafeNormalize(normal + bitangent * 0.75, normal) * distance, clipRange);
        float sampleBMinus = EvaluateOpacityOnly(samplePos + SafeNormalize(normal - bitangent * 0.75, normal) * distance, clipRange);

        occlusion += weight * (sampleCenter + 0.5 * (sampleTPlus + sampleTMinus + sampleBPlus + sampleBMinus));
        weightSum += weight * 3.0;
    }

    float averageOcclusion = weightSum > 0.0 ? occlusion / weightSum : 0.0;
    return 1.0 - saturate(averageOcclusion * _CinematicAoStrength);
}

float ComputeLightTransmittance(float3 samplePos, float3 lightDir, float clipRange)
{
    float3 safeLightDir = SafeNormalize(lightDir, float3(0.0, 0.0, 1.0));
    float stepSize = max(_StepSize * _CinematicShadowStepMultiplier, GetAverageVoxelSize());
    float2 lightHit = IntersectUnitBox(samplePos + safeLightDir * 1e-4, safeLightDir);
    float t = max(lightHit.x, 0.0) + stepSize;
    float tFar = lightHit.y;
    float transmittance = 1.0;
    int maxShadowSteps = clamp((int)_CinematicShadowMaxSteps, 1, 256);

    [loop]
    for (int i = 0; i < 256; ++i)
    {
        if (i >= maxShadowSteps || t > tFar || transmittance <= 0.01)
        {
            break;
        }

        float3 shadowPos = samplePos + safeLightDir * t;
        if (IsCutout(shadowPos) <= 0.5)
        {
            float density = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, shadowPos).r;
            float4 tf = EvaluateTransferFunction(shadowPos, density, clipRange);
            float extinction = saturate(tf.a * _Opacity * _CinematicExtinction);
            transmittance *= exp(-extinction);
        }

        t += stepSize;
    }

    return saturate(transmittance);
}

float3 ShadeCinematicVolume(float3 samplePos, float3 viewDir, float3 baseColor, float clipRange, float lightingMode)
{
    float3 normal;
    float gradientLength;
    if (!TryGetGradientNormal(samplePos, normal, gradientLength))
    {
        return baseColor * max(_CinematicAmbient, 0.1);
    }

    if (dot(normal, viewDir) < 0.0)
    {
        normal = -normal;
    }

    float3 lightDir = _CinematicLightFollowsCamera > 0.5
        ? viewDir
        : SafeNormalize(_LightDirOS.xyz, viewDir);
    float ndotl = saturate(dot(normal, lightDir));
    float3 halfVector = SafeNormalize(lightDir + viewDir, viewDir);
    float specularTerm = pow(saturate(dot(normal, halfVector)), max(_CinematicSpecPower, 1.0));

    float aoFactor = 1.0;
    if (lightingMode > 2.5 && lightingMode < 3.5)
    {
        aoFactor = ComputeLocalAmbientOcclusion(samplePos, normal, clipRange);
    }

    float transmittance = 1.0;
    if (lightingMode > 3.5)
    {
        transmittance = ComputeLightTransmittance(samplePos, lightDir, clipRange);
    }

    float shadowFactor = lerp(1.0, transmittance, _CinematicShadowStrength);
    float ambientFactor = lerp(1.0, aoFactor, 0.85);
    float diffuseFactor = lerp(1.0, aoFactor, 0.65);
    float lightIntensity = max(_CinematicLightIntensity, 0.0);

    float3 lit = baseColor * (_CinematicAmbient * ambientFactor);
    lit += lightIntensity * baseColor * (_CinematicDiffuse * ndotl * diffuseFactor * shadowFactor);
    lit += lightIntensity * _CinematicSpecular * specularTerm * shadowFactor;

    if (lightingMode > 3.5)
    {
        float scatterAmount = lightIntensity * (1.0 - transmittance) * saturate(0.35 + 0.65 * ndotl);
        lit = lerp(lit, lit + baseColor * scatterAmount, saturate(_CinematicScatterBlend));
    }

    return lit;
}

#endif
