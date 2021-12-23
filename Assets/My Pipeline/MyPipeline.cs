using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using Conditional = System.Diagnostics.ConditionalAttribute;

public class MyPipeline : RenderPipeline {

	const int maxVisibleLights = 16;

	const string cascadedShadowsHardKeyword = "_CASCADED_SHADOWS_HARD";
	const string cascadedShadowsSoftKeyword = "_CASCADED_SHADOWS_SOFT";
	const string shadowsHardKeyword = "_SHADOWS_HARD";
	const string shadowsSoftKeyword = "_SHADOWS_SOFT";

	static int visibleLightColorsId =
		Shader.PropertyToID("_VisibleLightColors");
	static int visibleLightDirectionsOrPositionsId =
		Shader.PropertyToID("_VisibleLightDirectionsOrPositions");
	static int visibleLightAttenuationsId =
		Shader.PropertyToID("_VisibleLightAttenuations");
	static int visibleLightSpotDirectionsId =
		Shader.PropertyToID("_VisibleLightSpotDirections");
	static int lightIndicesOffsetAndCountID =
		Shader.PropertyToID("unity_LightIndicesOffsetAndCount");
	static int shadowMapId = Shader.PropertyToID("_ShadowMap");
	static int cascadedShadowMapId = Shader.PropertyToID("_CascadedShadowMap");
	static int worldToShadowMatricesId =
		Shader.PropertyToID("_WorldToShadowMatrices");
	static int worldToShadowCascadeMatricesId =
		Shader.PropertyToID("_WorldToShadowCascadeMatrices");
	static int shadowBiasId = Shader.PropertyToID("_ShadowBias");
	static int shadowDataId = Shader.PropertyToID("_ShadowData");
	static int shadowMapSizeId = Shader.PropertyToID("_ShadowMapSize");
	static int cascadedShadowMapSizeId =
		Shader.PropertyToID("_CascadedShadowMapSize");
	static int cascadedShadoStrengthId =
		Shader.PropertyToID("_CascadedShadowStrength");
	static int globalShadowDataId = Shader.PropertyToID("_GlobalShadowData");
	static int cascadeCullingSpheresId =
		Shader.PropertyToID("_CascadeCullingSpheres");

	Vector4[] visibleLightColors = new Vector4[maxVisibleLights];
	Vector4[] visibleLightDirectionsOrPositions = new Vector4[maxVisibleLights];
	Vector4[] visibleLightAttenuations = new Vector4[maxVisibleLights];
	Vector4[] visibleLightSpotDirections = new Vector4[maxVisibleLights];

	CullingResults cull;

	RenderTexture shadowMap, cascadedShadowMap;
	Vector4[] shadowData = new Vector4[maxVisibleLights];
	Matrix4x4[] worldToShadowMatrices = new Matrix4x4[maxVisibleLights];
	Matrix4x4[] worldToShadowCascadeMatrices = new Matrix4x4[5];
	Vector4[] cascadeCullingSpheres = new Vector4[4];

	Material errorMaterial;

	CommandBuffer cameraBuffer = new CommandBuffer {
		name = "Render Camera"
	};

	CommandBuffer shadowBuffer = new CommandBuffer {
		name = "Render Shadows"
	};

	bool dynamicBatching;
	bool instancing;

	int shadowMapSize;
	int shadowTileCount;
	float shadowDistance;
	int shadowCascades;
	Vector3 shadowCascadeSplit;

	bool mainLightExists;

	public MyPipeline (
		bool dynamicBatching, bool instancing,
		int shadowMapSize, float shadowDistance,
		int shadowCascades, Vector3 shadowCascasdeSplit
	) {
		GraphicsSettings.lightsUseLinearIntensity = true;
		if (SystemInfo.usesReversedZBuffer) {
			worldToShadowCascadeMatrices[4].m33 = 1f;
		}


		this.dynamicBatching = dynamicBatching;
		this.instancing = instancing;

		this.shadowMapSize = shadowMapSize;
		this.shadowDistance = shadowDistance;
		this.shadowCascades = shadowCascades;
		this.shadowCascadeSplit = shadowCascasdeSplit;
	}

	protected override void Render (
		ScriptableRenderContext renderContext, Camera[] cameras
	) {
		foreach (var camera in cameras) {
			Render(renderContext, camera);
		}
	}

	void Render (ScriptableRenderContext context, Camera camera) {
		ScriptableCullingParameters cullingParameters;

		if (!camera.TryGetCullingParameters(camera, out cullingParameters)) {
			return;
		}
		cullingParameters.shadowDistance =
			Mathf.Min(shadowDistance, camera.farClipPlane);

#if UNITY_EDITOR
		if (camera.cameraType == CameraType.SceneView) {
			ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
		}
#endif

		cull = context.Cull(ref cullingParameters );
		
		if ( cull.visibleLights.Length > 0) {
			ConfigureLights();
			if (mainLightExists) {
				RenderCascadedShadows(context);
			}
			else {
				cameraBuffer.DisableShaderKeyword(cascadedShadowsHardKeyword);
				cameraBuffer.DisableShaderKeyword(cascadedShadowsSoftKeyword);
			}
			if (shadowTileCount > 0) {
				RenderShadows(context);
			}
			else {
				cameraBuffer.DisableShaderKeyword(shadowsHardKeyword);
				cameraBuffer.DisableShaderKeyword(shadowsSoftKeyword);
			}
		}
		else {
			cameraBuffer.SetGlobalVector(
				lightIndicesOffsetAndCountID, Vector4.zero
			);
			cameraBuffer.DisableShaderKeyword(cascadedShadowsHardKeyword);
			cameraBuffer.DisableShaderKeyword(cascadedShadowsSoftKeyword);
			cameraBuffer.DisableShaderKeyword(shadowsHardKeyword);
			cameraBuffer.DisableShaderKeyword(shadowsSoftKeyword);
		}

		context.SetupCameraProperties(camera);

		CameraClearFlags clearFlags = camera.clearFlags;
		cameraBuffer.ClearRenderTarget(
			(clearFlags & CameraClearFlags.Depth) != 0,
			(clearFlags & CameraClearFlags.Color) != 0,
			camera.backgroundColor
		);

		cameraBuffer.BeginSample("Render Camera");
		cameraBuffer.SetGlobalVectorArray(
			visibleLightColorsId, visibleLightColors
		);
		cameraBuffer.SetGlobalVectorArray(
			visibleLightDirectionsOrPositionsId, visibleLightDirectionsOrPositions
		);
		cameraBuffer.SetGlobalVectorArray(
			visibleLightAttenuationsId, visibleLightAttenuations
		);
		cameraBuffer.SetGlobalVectorArray(
			visibleLightSpotDirectionsId, visibleLightSpotDirections
		);
		context.ExecuteCommandBuffer(cameraBuffer);
		cameraBuffer.Clear();

		var sortingSettings = new SortingSettings(camera)
		{
			criteria = SortingCriteria.CommonOpaque
		};

		var drawingSettings = new DrawingSettings(new ShaderTagId("SRPDefaultUnlit"), sortingSettings)
		{
			enableDynamicBatching = dynamicBatching,
			enableInstancing = instancing
		};

		// JW: whats the equivalent now ?
		//if (cull.visibleLights.Length > 0) {

		//	drawingSettings.rendererConfiguration =
		//		RendererConfiguration.PerObjectLightIndices8;
		//}

		var filteringSettings = new FilteringSettings(RenderQueueRange.opaque );

		context.DrawRenderers(	cull, ref drawingSettings, ref filteringSettings );

		context.DrawSkybox(camera);

		sortingSettings.criteria = SortingCriteria.CommonTransparent;
		
		filteringSettings.renderQueueRange = RenderQueueRange.transparent;
		
		context.DrawRenderers(	cull, ref drawingSettings, ref filteringSettings 	);

		DrawDefaultPipeline(context, camera);

		cameraBuffer.EndSample("Render Camera");
		context.ExecuteCommandBuffer(cameraBuffer);
		cameraBuffer.Clear();

		context.Submit();

		if (shadowMap) {
			RenderTexture.ReleaseTemporary(shadowMap);
			shadowMap = null;
		}
		if (cascadedShadowMap) {
			RenderTexture.ReleaseTemporary(cascadedShadowMap);
			cascadedShadowMap = null;
		}
	}

	void ConfigureLights () {
		mainLightExists = false;
		shadowTileCount = 0;
		for (int i = 0; i < cull.visibleLights.Length; i++) {
			if (i == maxVisibleLights) {
				break;
			}
			VisibleLight light = cull.visibleLights[i];
			visibleLightColors[i] = light.finalColor;
			Vector4 attenuation = Vector4.zero;
			attenuation.w = 1f;
			Vector4 shadow = Vector4.zero;

			if (light.lightType == LightType.Directional) {
				Vector4 v = light.localToWorldMatrix.GetColumn(2);
				v.x = -v.x;
				v.y = -v.y;
				v.z = -v.z;
				visibleLightDirectionsOrPositions[i] = v;
				shadow = ConfigureShadows(i, light.light);
				shadow.z = 1f;
				if (i == 0 && shadow.x > 0f && shadowCascades > 0) {
					mainLightExists = true;
					shadowTileCount -= 1;
				}
			}
			else {
				visibleLightDirectionsOrPositions[i] =
					light.localToWorldMatrix.GetColumn(3);
				attenuation.x = 1f /
					Mathf.Max(light.range * light.range, 0.00001f);

				if (light.lightType == LightType.Spot) {
					Vector4 v = light.localToWorldMatrix.GetColumn(2);
					v.x = -v.x;
					v.y = -v.y;
					v.z = -v.z;
					visibleLightSpotDirections[i] = v;

					float outerRad = Mathf.Deg2Rad * 0.5f * light.spotAngle;
					float outerCos = Mathf.Cos(outerRad);
					float outerTan = Mathf.Tan(outerRad);
					float innerCos =
						Mathf.Cos(Mathf.Atan((46f / 64f) * outerTan));
					float angleRange = Mathf.Max(innerCos - outerCos, 0.001f);
					attenuation.z = 1f / angleRange;
					attenuation.w = -outerCos * attenuation.z;

					shadow = ConfigureShadows(i, light.light);
				}
			}

			visibleLightAttenuations[i] = attenuation;
			shadowData[i] = shadow;
		}

		if (mainLightExists || cull.visibleLights.Length > maxVisibleLights) {
			Unity.Collections.NativeArray<int> lightIndices = cull.GetLightIndexMap ( Unity.Collections.Allocator.Temp );


			if (mainLightExists) {
				lightIndices[0] = -1;
			}
			for (int i = maxVisibleLights; i < cull.visibleLights.Length; i++) {
				lightIndices[i] = -1;
			}
			cull.SetLightIndexMap(lightIndices);
		}
	}

	Vector4 ConfigureShadows (int lightIndex, Light shadowLight) {
		Vector4 shadow = Vector4.zero;
		Bounds shadowBounds;
		if (
			shadowLight.shadows != LightShadows.None &&
			cull.GetShadowCasterBounds(lightIndex, out shadowBounds)
		) {
			shadowTileCount += 1;
			shadow.x = shadowLight.shadowStrength;
			shadow.y =
				shadowLight.shadows == LightShadows.Soft ? 1f : 0f;
		}
		return shadow;
	}

	void RenderCascadedShadows (ScriptableRenderContext context) {
		float tileSize = shadowMapSize / 2;
		cascadedShadowMap = SetShadowRenderTarget();
		shadowBuffer.BeginSample("Render Shadows");
		shadowBuffer.SetGlobalVector(
			globalShadowDataId, new Vector4(0f, shadowDistance * shadowDistance)
		);
		context.ExecuteCommandBuffer(shadowBuffer);
		shadowBuffer.Clear();
		Light shadowLight = cull.visibleLights[0].light;
		shadowBuffer.SetGlobalFloat(
			shadowBiasId, shadowLight.shadowBias
		);
		var shadowDrawingSettings = new ShadowDrawingSettings( cull,0 );
		var tileMatrix = Matrix4x4.identity;
		tileMatrix.m00 = tileMatrix.m11 = 0.5f;

		for (int i = 0; i < shadowCascades; i++) {
			Matrix4x4 viewMatrix, projectionMatrix;
			ShadowSplitData splitData;
			cull.ComputeDirectionalShadowMatricesAndCullingPrimitives(
				0, i, shadowCascades, shadowCascadeSplit, (int)tileSize,
				shadowLight.shadowNearPlane,
				out viewMatrix, out projectionMatrix, out splitData
			);

			Vector2 tileOffset = ConfigureShadowTile(i, 2, tileSize);
			shadowBuffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
			context.ExecuteCommandBuffer(shadowBuffer);
			shadowBuffer.Clear();

			ShadowSplitData spData = shadowDrawingSettings.splitData;

			cascadeCullingSpheres[i] = spData.cullingSphere = splitData.cullingSphere;

			shadowDrawingSettings.splitData = spData;

			cascadeCullingSpheres[i].w *= splitData.cullingSphere.w;
			context.DrawShadows(ref shadowDrawingSettings);
			CalculateWorldToShadowMatrix(
				ref viewMatrix, ref projectionMatrix,
				out worldToShadowCascadeMatrices[i]
			);
			tileMatrix.m03 = tileOffset.x * 0.5f;
			tileMatrix.m13 = tileOffset.y * 0.5f;
			worldToShadowCascadeMatrices[i] =
				tileMatrix * worldToShadowCascadeMatrices[i];
		}

		shadowBuffer.DisableScissorRect();
		shadowBuffer.SetGlobalTexture(cascadedShadowMapId, cascadedShadowMap);
		shadowBuffer.SetGlobalVectorArray(
			cascadeCullingSpheresId, cascadeCullingSpheres
		);
		shadowBuffer.SetGlobalMatrixArray(
			worldToShadowCascadeMatricesId, worldToShadowCascadeMatrices
		);
		float invShadowMapSize = 1f / shadowMapSize;
		shadowBuffer.SetGlobalVector(
			cascadedShadowMapSizeId, new Vector4(
				invShadowMapSize, invShadowMapSize, shadowMapSize, shadowMapSize
			)
		);
		shadowBuffer.SetGlobalFloat(
			cascadedShadoStrengthId, shadowLight.shadowStrength
		);
		bool hard = shadowLight.shadows == LightShadows.Hard;
		CoreUtils.SetKeyword(shadowBuffer, cascadedShadowsHardKeyword, hard);
		CoreUtils.SetKeyword(shadowBuffer, cascadedShadowsSoftKeyword, !hard);
		shadowBuffer.EndSample("Render Shadows");
		context.ExecuteCommandBuffer(shadowBuffer);
		shadowBuffer.Clear();
	}

	void RenderShadows (ScriptableRenderContext context) {
		int split;
		if (shadowTileCount <= 1) {
			split = 1;
		}
		else if (shadowTileCount <= 4) {
			split = 2;
		}
		else if (shadowTileCount <= 9) {
			split = 3;
		}
		else {
			split = 4;
		}

		float tileSize = shadowMapSize / split;
		float tileScale = 1f / split;
		shadowMap = SetShadowRenderTarget();
		shadowBuffer.BeginSample("Render Shadows");
		shadowBuffer.SetGlobalVector(
			globalShadowDataId, new Vector4(
				tileScale, shadowDistance * shadowDistance
			)
		);
		context.ExecuteCommandBuffer(shadowBuffer);
		shadowBuffer.Clear();

		int tileIndex = 0;
		bool hardShadows = false;
		bool softShadows = false;
		for (int i = mainLightExists ? 1 : 0; i < cull.visibleLights.Length; i++) {
			if (i == maxVisibleLights) {
				break;
			}
			if (shadowData[i].x <= 0f) {
				continue;
			}

			Matrix4x4 viewMatrix, projectionMatrix;
			ShadowSplitData splitData;
			bool validShadows;
			if (shadowData[i].z > 0f) {
				validShadows =
					cull.ComputeDirectionalShadowMatricesAndCullingPrimitives(
						i, 0, 1, Vector3.right, (int)tileSize,
						cull.visibleLights[i].light.shadowNearPlane,
						out viewMatrix, out projectionMatrix, out splitData
					);
			}
			else {
				validShadows =
					cull.ComputeSpotShadowMatricesAndCullingPrimitives(
						i, out viewMatrix, out projectionMatrix, out splitData
					);
			}
			if (!validShadows) {
				shadowData[i].x = 0f;
				continue;
			}

			Vector2 tileOffset = ConfigureShadowTile(tileIndex, split, tileSize);
			shadowData[i].z = tileOffset.x * tileScale;
			shadowData[i].w = tileOffset.y * tileScale;
			shadowBuffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
			shadowBuffer.SetGlobalFloat(
				shadowBiasId, cull.visibleLights[i].light.shadowBias
			);
			context.ExecuteCommandBuffer(shadowBuffer);
			shadowBuffer.Clear();

			var shadowDrawingSettings = new ShadowDrawingSettings(cull, i);

			ShadowSplitData spData = shadowDrawingSettings.splitData;
			spData.cullingSphere = splitData.cullingSphere;

			shadowDrawingSettings.splitData = spData;

			context.DrawShadows(ref shadowDrawingSettings);
			CalculateWorldToShadowMatrix(
				ref viewMatrix, ref projectionMatrix, out worldToShadowMatrices[i]
			);

			tileIndex += 1;
			if (shadowData[i].y <= 0f) {
				hardShadows = true;
			}
			else {
				softShadows = true;
			}
		}

		shadowBuffer.DisableScissorRect();
		shadowBuffer.SetGlobalTexture(shadowMapId, shadowMap);
		shadowBuffer.SetGlobalMatrixArray(
			worldToShadowMatricesId, worldToShadowMatrices
		);
		shadowBuffer.SetGlobalVectorArray(shadowDataId, shadowData);
		float invShadowMapSize = 1f / shadowMapSize;
		shadowBuffer.SetGlobalVector(
			shadowMapSizeId, new Vector4(
				invShadowMapSize, invShadowMapSize, shadowMapSize, shadowMapSize
			)
		);
		CoreUtils.SetKeyword(shadowBuffer, shadowsHardKeyword, hardShadows);
		CoreUtils.SetKeyword(shadowBuffer, shadowsSoftKeyword, softShadows);
		shadowBuffer.EndSample("Render Shadows");
		context.ExecuteCommandBuffer(shadowBuffer);
		shadowBuffer.Clear();
	}

	RenderTexture SetShadowRenderTarget () {
		RenderTexture texture = RenderTexture.GetTemporary(
			shadowMapSize, shadowMapSize, 16, RenderTextureFormat.Shadowmap
		);
		texture.filterMode = FilterMode.Bilinear;
		texture.wrapMode = TextureWrapMode.Clamp;

		CoreUtils.SetRenderTarget(
			shadowBuffer, texture,
			RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
			ClearFlag.Depth
		);
		return texture;
	}

	Vector2 ConfigureShadowTile (int tileIndex, int split, float tileSize) {
		Vector2 tileOffset;
		tileOffset.x = tileIndex % split;
		tileOffset.y = tileIndex / split;
		var tileViewport = new Rect(
			tileOffset.x * tileSize, tileOffset.y * tileSize, tileSize, tileSize
		);
		shadowBuffer.SetViewport(tileViewport);
		shadowBuffer.EnableScissorRect(new Rect(
			tileViewport.x + 4f, tileViewport.y + 4f,
			tileSize - 8f, tileSize - 8f
		));
		return tileOffset;
	}

	void CalculateWorldToShadowMatrix (
		ref Matrix4x4 viewMatrix, ref Matrix4x4 projectionMatrix,
		out Matrix4x4 worldToShadowMatrix
	) {
		if (SystemInfo.usesReversedZBuffer) {
			projectionMatrix.m20 = -projectionMatrix.m20;
			projectionMatrix.m21 = -projectionMatrix.m21;
			projectionMatrix.m22 = -projectionMatrix.m22;
			projectionMatrix.m23 = -projectionMatrix.m23;
		}
		var scaleOffset = Matrix4x4.identity;
		scaleOffset.m00 = scaleOffset.m11 = scaleOffset.m22 = 0.5f;
		scaleOffset.m03 = scaleOffset.m13 = scaleOffset.m23 = 0.5f;
		worldToShadowMatrix = scaleOffset * (projectionMatrix * viewMatrix);
	}

	[Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
	void DrawDefaultPipeline (ScriptableRenderContext context, Camera camera) {
		if (errorMaterial == null) {
			Shader errorShader = Shader.Find("Hidden/InternalErrorShader");
			errorMaterial = new Material(errorShader) {
				hideFlags = HideFlags.HideAndDontSave
			};
		}
		var drawingSettings = new DrawingSettings(
			new ShaderTagId("ForwardBase"), new SortingSettings(camera)
		);
		drawingSettings.SetShaderPassName(1, new ShaderTagId("PrepassBase"));
		drawingSettings.SetShaderPassName(2, new ShaderTagId("Always"));
		drawingSettings.SetShaderPassName(3, new ShaderTagId("Vertex"));
		drawingSettings.SetShaderPassName(4, new ShaderTagId("VertexLMRGBM"));
		drawingSettings.SetShaderPassName(5, new ShaderTagId("VertexLM"));
		drawingSettings.overrideMaterial  = errorMaterial;

		var filteringSettings = new FilteringSettings();



		context.DrawRenderers(
			cull, ref drawingSettings, ref filteringSettings 
		);
	}
}