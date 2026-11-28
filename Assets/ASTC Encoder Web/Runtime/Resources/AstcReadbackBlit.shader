Shader "Hidden/Aperture/AstcReadbackBlit"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        HLSLINCLUDE
        #include "UnityCG.cginc"

        struct appdata
        {
            float4 vertex : POSITION;
            float2 uv     : TEXCOORD0;
        };

        struct v2f
        {
            float4 pos : SV_POSITION;
            float2 uv  : TEXCOORD0;
        };

        v2f vert(appdata v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.uv;
            return o;
        }

        float _SourceMip;
        float _Slice;
        float _Face;
        float4 _TexelSize; // (1/w, 1/h, w, h)
        ENDHLSL

        // Pass 0: 2D texture / RenderTexture at _SourceMip
        Pass
        {
            Name "Blit2D"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag2D
            #pragma target 3.0

            sampler2D _MainTex;

            float4 frag2D(v2f i) : SV_Target
            {
                return tex2Dlod(_MainTex, float4(i.uv, 0, _SourceMip));
            }
            ENDHLSL
        }

        // Pass 1: 2x2 Box downsample for mipmap generation (in linear/render target space)
        Pass
        {
            Name "BoxDownsample"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragDownsample
            #pragma target 3.0

            sampler2D _MainTex;

            float4 fragDownsample(v2f i) : SV_Target
            {
                float2 halfTexel = _TexelSize.xy * 0.5;
                float4 c0 = tex2Dlod(_MainTex, float4(i.uv + float2(-halfTexel.x, -halfTexel.y), 0, _SourceMip));
                float4 c1 = tex2Dlod(_MainTex, float4(i.uv + float2( halfTexel.x, -halfTexel.y), 0, _SourceMip));
                float4 c2 = tex2Dlod(_MainTex, float4(i.uv + float2(-halfTexel.x,  halfTexel.y), 0, _SourceMip));
                float4 c3 = tex2Dlod(_MainTex, float4(i.uv + float2( halfTexel.x,  halfTexel.y), 0, _SourceMip));
                return (c0 + c1 + c2 + c3) * 0.25;
            }
            ENDHLSL
        }

        // Pass 2: Cubemap face (_Face = 0..5, _SourceMip)
        Pass
        {
            Name "BlitCubeFace"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragCube
            #pragma target 3.0

            samplerCUBE _CubeTex;

            float3 FaceUvToDir(int face, float2 uv)
            {
                // Map uv in [0,1] to [-1,1]; invert Y to match Unity Cubemap face orientation
                float2 st = uv * 2.0 - 1.0;
                st.y = -st.y;
                if (face == 0) return float3( 1.0,  st.y, -st.x); // +X
                if (face == 1) return float3(-1.0,  st.y,  st.x); // -X
                if (face == 2) return float3( st.x,  1.0, -st.y); // +Y
                if (face == 3) return float3( st.x, -1.0,  st.y); // -Y
                if (face == 4) return float3( st.x,  st.y,  1.0); // +Z
                return                float3(-st.x,  st.y, -1.0); // -Z
            }

            float4 fragCube(v2f i) : SV_Target
            {
                float3 dir = normalize(FaceUvToDir((int)_Face, i.uv));
                return texCUBElod(_CubeTex, float4(dir, _SourceMip));
            }
            ENDHLSL
        }

        // Pass 3: Texture2DArray slice (_Slice, _SourceMip)
        Pass
        {
            Name "Blit2DArray"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragArray
            #pragma target 3.5

            UNITY_DECLARE_TEX2DARRAY(_ArrayTex);

            float4 fragArray(v2f i) : SV_Target
            {
                return UNITY_SAMPLE_TEX2DARRAY_LOD(_ArrayTex, float3(i.uv, _Slice), _SourceMip);
            }
            ENDHLSL
        }
    }
}
