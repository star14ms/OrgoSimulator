Shader "OrgoSimulator/TriangleClipCircle"
{
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _ClipCenter ("Clip Center (object space)", Vector) = (0, 1, 0, 0)
        _ClipRadiusX ("Clip Radius X (object space)", Float) = 0.5
        _ClipRadiusY ("Clip Radius Y (object space)", Float) = 0.5
    }
    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float3 objectPos : TEXCOORD1;
            };

            sampler2D _MainTex;
            fixed4 _Color;
            float4 _ClipCenter;
            float _ClipRadiusX;
            float _ClipRadiusY;

            v2f vert(appdata_t v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.texcoord = v.texcoord;
                o.objectPos = v.vertex.xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.texcoord) * i.color * _Color;
                float2 clipCenter = _ClipCenter.xy;
                float2 d = i.objectPos.xy - clipCenter;
                float ellipse = (d.x * d.x) / (_ClipRadiusX * _ClipRadiusX) + (d.y * d.y) / (_ClipRadiusY * _ClipRadiusY);
                if (ellipse < 1.0)
                    discard;
                return col;
            }
            ENDCG
        }
    }
}
