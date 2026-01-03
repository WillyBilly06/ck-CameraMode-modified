using System;
using System.Collections;
using System.Collections.Generic;
using CameraMode.Utilities;
using PugMod;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace CameraMode.Capture {
	public abstract class CaptureBase {
		public abstract float Progress { get; }
		public virtual float2 DetailedProgress { get; } = float2.zero;
		
		public virtual bool CanPauseSimulation { get; } = false;

		public abstract IEnumerator GetCoroutine(Action<byte[]> callback);
	}

	public class ScreenshotCapture : CaptureBase, IDisposable {
		public override float Progress => 1f;

		private Texture2D _captureTexture;
		private RenderTexture _renderTexture;
		
		public override IEnumerator GetCoroutine(Action<byte[]> callback) {
			var captureResScale = Config.Instance.CaptureResolutionScale;
			var captureQuality = Config.Instance.CaptureQuality;
			
			var gameCamera = API.Rendering.GameCamera.camera;

			var outputSize = new int2(
				Mathf.CeilToInt(Constants.kScreenPixelWidth * captureResScale),
				Mathf.CeilToInt(Constants.kScreenPixelHeight * captureResScale)
			);
			var outputPixels = new byte[(outputSize.x * outputSize.y) * 4];
			
			yield return new WaitForEndOfFrame();
			
			_captureTexture = new Texture2D(Constants.kScreenPixelWidth * captureResScale, Constants.kScreenPixelHeight * captureResScale, TextureFormat.RGB24, false);
			_renderTexture = new RenderTexture(Constants.kScreenPixelWidth * captureResScale, Constants.kScreenPixelHeight * captureResScale, 24);
			
			var oldActiveRenderTexture = RenderTexture.active;
			var oldTargetTexture = gameCamera.targetTexture;

			RenderTexture.active = _renderTexture;
			gameCamera.targetTexture = _renderTexture;
			gameCamera.Render();

			_captureTexture.ReadPixels(new Rect(0, 0, _captureTexture.width, _captureTexture.height), 0, 0);
			_captureTexture.Apply();

			Utils.CopyToPixelBuffer(_captureTexture, ref outputPixels, 0, 0, outputSize.x, outputSize.y);

			gameCamera.targetTexture = oldTargetTexture;
			RenderTexture.active = oldActiveRenderTexture;
			
			var encodedImageData = captureQuality.EncodeArrayToImage(captureResScale, outputPixels, GraphicsFormat.R8G8B8A8_SRGB, (uint) outputSize.x, (uint) outputSize.y);
			callback?.Invoke(encodedImageData);
		}

		public void Dispose() {
			if (_captureTexture != null)
				Object.Destroy(_captureTexture);
			if (_renderTexture != null)
				Object.Destroy(_renderTexture);
		}
	}
	
	public class FrameCapture : CaptureBase, IDisposable {
		// Memory limits
		private const long MaxFinalImageBytes = 1024L * 1024L * 1024L; // 1GB for final image
		private const int MaxFinalDimension = 16384; // Max texture dimension in Unity

		public override float Progress => (float) _areasCaptured / _totalSteps;
		public override float2 DetailedProgress => new(_areasCaptured, _areasToCapture);
		public override bool CanPauseSimulation => true;

		private readonly CaptureFrame _frame;
		
		private int _areasCaptured;
		private readonly int _areasToCapture;
		private int _totalSteps;

		private Texture2D _captureTexture;
		private RenderTexture _renderTexture;
		
		public FrameCapture(CaptureFrame frame) {
			_frame = frame;

			var frameSize = _frame.Size;
			
			_areasCaptured = 0;
			_areasToCapture = Mathf.CeilToInt((frameSize.x * Constants.PIXELS_PER_UNIT_F) / Constants.kScreenPixelWidth) 
			                * Mathf.CeilToInt((frameSize.y * Constants.PIXELS_PER_UNIT_F) / Constants.kScreenPixelHeight);
			_totalSteps = _areasToCapture + 1; // +1 for stitching phase
		}

		public override IEnumerator GetCoroutine(Action<byte[]> callback) {
			Manager.camera.currentCameraStyle = CameraManager.CameraControlStyle.Static;
			Manager.camera.manualControlTargetPosition = Manager.main.player.GetEntityPosition();
			
			var captureResScale = Config.Instance.CaptureResolutionScale;
			var captureQuality = Config.Instance.CaptureQuality;
			var framePosition = _frame.Position;
			var frameSize = _frame.Size;
			
			var gameCamera = API.Rendering.GameCamera.camera;

			var chunks = new int2(
				Mathf.CeilToInt((frameSize.x * Constants.PIXELS_PER_UNIT_F) / Constants.kScreenPixelWidth),
				Mathf.CeilToInt((frameSize.y * Constants.PIXELS_PER_UNIT_F) / Constants.kScreenPixelHeight)
			);
			
			// Calculate the effective resolution scale based on final image size limits
			var effectiveResScale = captureResScale;
			var chunkSize = new int2(
				Constants.kScreenPixelWidth * effectiveResScale,
				Constants.kScreenPixelHeight * effectiveResScale
			);
			var outputSize = new int2(chunks.x * chunkSize.x, chunks.y * chunkSize.y);
			
			// Auto-reduce resolution if final image would be too large
			while (effectiveResScale > 1 && 
			       ((long)outputSize.x * outputSize.y * 4 > MaxFinalImageBytes || 
			        outputSize.x > MaxFinalDimension || outputSize.y > MaxFinalDimension)) {
				effectiveResScale--;
				chunkSize = new int2(
					Constants.kScreenPixelWidth * effectiveResScale,
					Constants.kScreenPixelHeight * effectiveResScale
				);
				outputSize = new int2(chunks.x * chunkSize.x, chunks.y * chunkSize.y);
			}
			
			// If still too large at 1x, we need to downsample each tile
			var downsampleFactor = 1;
			while ((long)outputSize.x * outputSize.y * 4 > MaxFinalImageBytes || 
			       outputSize.x > MaxFinalDimension || outputSize.y > MaxFinalDimension) {
				downsampleFactor++;
				var reducedChunkSize = new int2(
					Mathf.Max(Constants.kScreenPixelWidth / downsampleFactor, 64),
					Mathf.Max(Constants.kScreenPixelHeight / downsampleFactor, 64)
				);
				outputSize = new int2(chunks.x * reducedChunkSize.x, chunks.y * reducedChunkSize.y);
				chunkSize = reducedChunkSize;
			}
			
			var screenUnitSize = new float2(
				Constants.kScreenPixelWidth / Constants.PIXELS_PER_UNIT_F,
				Constants.kScreenPixelHeight / Constants.PIXELS_PER_UNIT_F
			);
			
			var finalMemoryMB = (long)outputSize.x * outputSize.y * 4 / (1024 * 1024);
			if (effectiveResScale != captureResScale || downsampleFactor > 1) {
				Utils.DisplayChatMessage($"Large area: auto-adjusted to fit ({outputSize.x}x{outputSize.y}, {finalMemoryMB}MB)");
			}
			
			yield return null;

			// Create capture texture at full resolution (for quality), will downsample if needed
			var captureSize = new int2(
				Constants.kScreenPixelWidth * effectiveResScale,
				Constants.kScreenPixelHeight * effectiveResScale
			);
			_captureTexture = new Texture2D(captureSize.x, captureSize.y, TextureFormat.RGB24, false);
			_renderTexture = new RenderTexture(captureSize.x, captureSize.y, 24);

			// Store all tiles as compressed JPG (much smaller than raw)
			var tileImages = new List<byte[]>(_areasToCapture);
			var tilePositions = new List<int2>(_areasToCapture);
			
			Utils.DisplayChatMessage($"Capturing {_areasToCapture} tiles...");
			yield return null;
			
			var x = 0;
			var y = 0;
			var direction = 1;

			// PHASE 1: Capture all tiles as compressed images
			var areaLoadWaitTime = Config.Instance.AreaLoadWaitTime;
			for (var i = 0; i < chunks.x * chunks.y; i++) {
				Manager.camera.manualControlTargetPosition = new Vector3(
					framePosition.x + (screenUnitSize.x / 2f) - 0.5f + (screenUnitSize.x * x),
					Manager.camera.manualControlTargetPosition.y,
					framePosition.y + (screenUnitSize.y / 2f) - 0.5f + (screenUnitSize.y * y)
				);

				yield return new WaitForSeconds(areaLoadWaitTime);
				Manager.camera.cameraMovementStyle = CameraManager.CameraMovementStyle.Instant;
				yield return new WaitForSeconds(0.05f);
				yield return new WaitForEndOfFrame();
				Manager.camera.cameraMovementStyle = CameraManager.CameraMovementStyle.Smooth;

				var oldActiveRenderTexture = RenderTexture.active;
				var oldTargetTexture = gameCamera.targetTexture;

				RenderTexture.active = _renderTexture;
				gameCamera.targetTexture = _renderTexture;
				gameCamera.Render();

				_captureTexture.ReadPixels(new Rect(0, 0, _captureTexture.width, _captureTexture.height), 0, 0);
				_captureTexture.Apply();

				gameCamera.targetTexture = oldTargetTexture;
				RenderTexture.active = oldActiveRenderTexture;

				// Encode tile as JPG (high quality, ~10x smaller than raw)
				var tileData = _captureTexture.EncodeToJPG(95);
				tileImages.Add(tileData);
				tilePositions.Add(new int2(x, y));

				_areasCaptured++;

				// Periodic GC to keep memory in check
				if (_areasCaptured % 20 == 0) {
					GC.Collect();
					yield return null;
				}

				y += direction;
				if (y < 0 || y >= chunks.y) {
					x++;
					y -= direction;
					direction *= -1;
				}
			}
			
			// Free capture textures
			if (_captureTexture != null) {
				Object.Destroy(_captureTexture);
				_captureTexture = null;
			}
			if (_renderTexture != null) {
				Object.Destroy(_renderTexture);
				_renderTexture = null;
			}
			GC.Collect();
			yield return null;

			// PHASE 2: Stitch tiles into final image
			Utils.DisplayChatMessage($"Stitching {_areasToCapture} tiles into {outputSize.x}x{outputSize.y} image...");
			yield return null;
			
			// Allocate final pixel buffer
			byte[] outputPixels;
			try {
				outputPixels = new byte[(long)outputSize.x * outputSize.y * 4];
			} catch (OutOfMemoryException) {
				Utils.DisplayChatMessage("Out of memory during stitch! Area too large.");
				callback?.Invoke(null);
				yield break;
			}
			
			// Temp texture for decoding tiles
			var tempTexture = new Texture2D(2, 2, TextureFormat.RGB24, false);
			
			for (var i = 0; i < tileImages.Count; i++) {
				var tileData = tileImages[i];
				var tilePos = tilePositions[i];
				
				// Decode JPG back to texture
				tempTexture.LoadImage(tileData);
				
				// If we need to downsample, resize the texture
				if (downsampleFactor > 1 || tempTexture.width != chunkSize.x || tempTexture.height != chunkSize.y) {
					var resizedTexture = ResizeTexture(tempTexture, chunkSize.x, chunkSize.y);
					if (resizedTexture != tempTexture) {
						Object.Destroy(tempTexture);
						tempTexture = resizedTexture;
					}
				}
				
				// Copy to output buffer
				Utils.CopyToPixelBuffer(tempTexture, ref outputPixels, tilePos.x * chunkSize.x, tilePos.y * chunkSize.y, outputSize.x, outputSize.y);
				
				// Free the tile data
				tileImages[i] = null;
				
				// Periodic GC
				if (i % 10 == 0) {
					GC.Collect();
					yield return null;
				}
			}
			
			Object.Destroy(tempTexture);
			tileImages.Clear();
			tilePositions.Clear();
			GC.Collect();
			
			_areasCaptured = _totalSteps; // Stitching complete
			
			// PHASE 3: Encode final image
			Utils.DisplayChatMessage("Encoding final image...");
			yield return null;
			
			byte[] encodedImageData;
			try {
				encodedImageData = captureQuality.EncodeArrayToImage(1, outputPixels, GraphicsFormat.R8G8B8A8_SRGB, (uint) outputSize.x, (uint) outputSize.y);
			} catch (Exception e) {
				Utils.DisplayChatMessage($"Encoding failed: {e.Message}");
				callback?.Invoke(null);
				yield break;
			}
			
			outputPixels = null;
			GC.Collect();
			
			callback?.Invoke(encodedImageData);
		}
		
		private Texture2D ResizeTexture(Texture2D source, int targetWidth, int targetHeight) {
			var rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			rt.filterMode = FilterMode.Bilinear;
			
			RenderTexture.active = rt;
			Graphics.Blit(source, rt);
			
			var result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGB24, false);
			result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
			result.Apply();
			
			RenderTexture.active = null;
			RenderTexture.ReleaseTemporary(rt);
			
			return result;
		}

		public void Dispose() {
			if (_captureTexture != null)
				Object.Destroy(_captureTexture);
			if (_renderTexture != null)
				Object.Destroy(_renderTexture);
		}
	}
}