Shader "Grass/InstancedGrassUnlit"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _WindFrequency ("Wind Frequency", Float) = 0.5
        _WindStrength ("Wind Strength", Float) = 0.5
        _WindDirection ("Wind Direction", Vector) = (1, 0, 0, 0)
        _ColliderFlattenHeight ("Collider Flatten Height", Range(0, 1)) = 0.1
        _ColliderTransitionWidth ("Collider Transition Width", Float) = 0.1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode"="UniversalForward" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:setup
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv : TEXCOORD0;
                float height : TEXCOORD1;
                float4 positionCS : SV_POSITION;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
                float _WindFrequency;
                float _WindStrength;
                float4 _WindDirection;
                float _ColliderFlattenHeight;
                float _ColliderTransitionWidth;
            CBUFFER_END

            #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                StructuredBuffer<float4> _PositionBuffer;
            #endif

            int _ColliderCount;
            float4 _Colliders[8];

            float2 GetWindDirectionXZ()
            {
                float2 direction = _WindDirection.xz;
                float lengthSq = dot(direction, direction);
                if (lengthSq <= 0.0001)
                {
                    return float2(1.0, 0.0);
                }

                return normalize(direction);
            }

            float3 SimpleWind(float3 localPos, float3 worldPos, float time)
            {
                float2 windDir = GetWindDirectionXZ();
                float primaryWave = sin(time * _WindFrequency + dot(worldPos.xz, windDir) * 0.2);
                float secondaryWave = sin(time * (_WindFrequency * 0.73) + dot(worldPos.xz, float2(-windDir.y, windDir.x)) * 0.1) * 0.5;
                float sway = (primaryWave + secondaryWave) * _WindStrength * localPos.y;
                return float3(windDir.x, 0.0, windDir.y) * sway;
            }

            float3 ApplyColliderBend(float3 worldPos, float3 rootWorldPos, float height01)
            {
                float3 bentWorldPos = worldPos;

                [unroll]
                for (int i = 0; i < 8; i++)
                {
                    if (i >= _ColliderCount) break;

                    float3 colliderPos = _Colliders[i].xyz;
                    float radius = _Colliders[i].w;
                    float2 deltaXZ = rootWorldPos.xz - colliderPos.xz;
                    float dist = length(deltaXZ);

                    float innerRadius = max(radius - _ColliderTransitionWidth, 0.0);
                    float outerRadius = radius + _ColliderTransitionWidth;
                    if (dist > outerRadius) continue;

                    float3 rootToVertex = bentWorldPos - rootWorldPos;
                    float bladeLength = length(rootToVertex);
                    if (bladeLength <= 0.0001) continue;

                    float3 originalDir = rootToVertex / bladeLength;
                    float2 dirXZ = dist > 0.0001 ? deltaXZ / dist : float2(0.0, 1.0);
                    float restoreToDefault = smoothstep(innerRadius, outerRadius, dist);
                    float flattenAmount = 1.0 - restoreToDefault;
                    float heightMask = smoothstep(0.05, 0.2, height01);
                    float3 flattenedDir = normalize(float3(dirXZ.x, _ColliderFlattenHeight, dirXZ.y));
                    float3 targetDir = normalize(lerp(flattenedDir, originalDir, restoreToDefault));
                    float bendWeight = flattenAmount * heightMask;

                    bentWorldPos = rootWorldPos + normalize(lerp(originalDir, targetDir, bendWeight)) * bladeLength;
                }

                return bentWorldPos;
            }

            void setup()
            {
                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    float4 data = _PositionBuffer[unity_InstanceID];
                    float3 pos = data.xyz;
                    float rot = data.w;

                    float s;
                    float c;
                    sincos(radians(rot), s, c);

                    unity_ObjectToWorld = float4x4(
                        c, 0, s, pos.x,
                        0, 1, 0, pos.y,
                        -s, 0, c, pos.z,
                        0, 0, 0, 1
                    );
                #endif
            }

            float3 GetAnimatedLocalPos(float3 localPos, float3 baseWorldPos)
            {
                localPos += SimpleWind(localPos, baseWorldPos, _Time.y);
                localPos.x *= 0.1 * (1.0 - localPos.y);
                localPos.z *= 0.02;
                return localPos;
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);

                float3 rootWorldPos = mul(unity_ObjectToWorld, float4(0, 0, 0, 1)).xyz;
                float3 baseWorldPos = mul(unity_ObjectToWorld, input.positionOS).xyz;
                float3 localPos = GetAnimatedLocalPos(input.positionOS.xyz, baseWorldPos);
                float3 worldPos = mul(unity_ObjectToWorld, float4(localPos, 1.0)).xyz;
                float height01 = saturate(localPos.y);
                worldPos = ApplyColliderBend(worldPos, rootWorldPos, height01);

                output.positionCS = TransformWorldToHClip(worldPos);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.height = height01;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * _Color;
                half3 topColor = half3(0.5, 0.8, 0.3);
                half3 bottomColor = half3(0.2, 0.5, 0.1);
                col.rgb *= lerp(bottomColor, topColor, input.height);
                return col;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex VertShadow
            #pragma fragment FragShadow
            #pragma multi_compile_shadowcaster
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:setup
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                StructuredBuffer<float4> _PositionBuffer;
            #endif

            int _ColliderCount;
            float4 _Colliders[8];

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
                float _WindFrequency;
                float _WindStrength;
                float4 _WindDirection;
                float _ColliderFlattenHeight;
                float _ColliderTransitionWidth;
            CBUFFER_END

            float2 GetWindDirectionXZ()
            {
                float2 direction = _WindDirection.xz;
                float lengthSq = dot(direction, direction);
                if (lengthSq <= 0.0001)
                {
                    return float2(1.0, 0.0);
                }

                return normalize(direction);
            }

            float3 SimpleWind(float3 localPos, float3 worldPos, float time)
            {
                float2 windDir = GetWindDirectionXZ();
                float primaryWave = sin(time * _WindFrequency + dot(worldPos.xz, windDir) * 0.2);
                float secondaryWave = sin(time * (_WindFrequency * 0.73) + dot(worldPos.xz, float2(-windDir.y, windDir.x)) * 0.1) * 0.5;
                float sway = (primaryWave + secondaryWave) * _WindStrength * localPos.y;
                return float3(windDir.x, 0.0, windDir.y) * sway;
            }

            float3 ApplyColliderBend(float3 worldPos, float3 rootWorldPos, float height01)
            {
                float3 bentWorldPos = worldPos;

                [unroll]
                for (int i = 0; i < 8; i++)
                {
                    if (i >= _ColliderCount) break;

                    float3 colliderPos = _Colliders[i].xyz;
                    float radius = _Colliders[i].w;
                    float2 deltaXZ = rootWorldPos.xz - colliderPos.xz;
                    float dist = length(deltaXZ);

                    float innerRadius = max(radius - _ColliderTransitionWidth, 0.0);
                    float outerRadius = radius + _ColliderTransitionWidth;
                    if (dist > outerRadius) continue;

                    float3 rootToVertex = bentWorldPos - rootWorldPos;
                    float bladeLength = length(rootToVertex);
                    if (bladeLength <= 0.0001) continue;

                    float3 originalDir = rootToVertex / bladeLength;
                    float2 dirXZ = dist > 0.0001 ? deltaXZ / dist : float2(0.0, 1.0);
                    float restoreToDefault = smoothstep(innerRadius, outerRadius, dist);
                    float flattenAmount = 1.0 - restoreToDefault;
                    float heightMask = smoothstep(0.05, 0.2, height01);
                    float3 flattenedDir = normalize(float3(dirXZ.x, _ColliderFlattenHeight, dirXZ.y));
                    float3 targetDir = normalize(lerp(flattenedDir, originalDir, restoreToDefault));
                    float bendWeight = flattenAmount * heightMask;

                    bentWorldPos = rootWorldPos + normalize(lerp(originalDir, targetDir, bendWeight)) * bladeLength;
                }

                return bentWorldPos;
            }

            void setup()
            {
                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    float4 data = _PositionBuffer[unity_InstanceID];
                    float3 pos = data.xyz;
                    float rot = data.w;

                    float s;
                    float c;
                    sincos(radians(rot), s, c);

                    unity_ObjectToWorld = float4x4(
                        c, 0, s, pos.x,
                        0, 1, 0, pos.y,
                        -s, 0, c, pos.z,
                        0, 0, 0, 1
                    );
                #endif
            }

            float3 GetAnimatedLocalPos(float3 localPos, float3 baseWorldPos)
            {
                localPos += SimpleWind(localPos, baseWorldPos, _Time.y);
                localPos.x *= 0.1 * (1.0 - localPos.y);
                localPos.z *= 0.02;
                return localPos;
            }

            float4 GetShadowPositionHClip(float3 positionWS, float3 normalWS)
            {
                float3 biasedPositionWS = ApplyShadowBias(positionWS, normalWS, _MainLightPosition.xyz);
                float4 positionCS = TransformWorldToHClip(biasedPositionWS);

                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #endif

                return positionCS;
            }

            Varyings VertShadow(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);

                float3 rootWorldPos = mul(unity_ObjectToWorld, float4(0, 0, 0, 1)).xyz;
                float3 baseWorldPos = mul(unity_ObjectToWorld, input.positionOS).xyz;
                float3 localPos = GetAnimatedLocalPos(input.positionOS.xyz, baseWorldPos);
                float3 worldPos = mul(unity_ObjectToWorld, float4(localPos, 1.0)).xyz;
                worldPos = ApplyColliderBend(worldPos, rootWorldPos, saturate(localPos.y));
                float3 normalWS = TransformObjectToWorldDir(input.normalOS);

                output.positionCS = GetShadowPositionHClip(worldPos, normalWS);
                return output;
            }

            half4 FragShadow(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
