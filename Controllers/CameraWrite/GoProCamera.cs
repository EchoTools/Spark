using System;
using System.Numerics;
using System.Threading.Tasks;
using EchoVRAPI;

namespace Spark
{
	public class GoProCamera : CameraModule
	{
		private Vector3 smoothPos = Vector3.Zero;
		private Quaternion smoothRot = Quaternion.Identity;
		private bool initialized = false;

		protected override Task Update(CameraTransform cameraTransform, float deltaTime)
		{
			// Use the already-fetched frame to avoid network jitter
			Frame frame = Program.lastFrame;
			if (frame == null) return Task.CompletedTask;

			// Get the target player
			string playerName = SparkSettings.instance.goProPlayerName;
			Player targetPlayer = null;

			if (!string.IsNullOrEmpty(playerName))
			{
				targetPlayer = frame.GetPlayer(playerName);
			}

			if (targetPlayer == null)
			{
				targetPlayer = frame.GetPlayer(frame.client_name);
			}

			if (targetPlayer == null) return Task.CompletedTask;

			// Latch directly to the hand position with a small offset right and up
			Vector3 handPos = SparkSettings.instance.goProTargetHand == 0
				? targetPlayer.lhand.Position
				: targetPlayer.rhand.Position;

			Vector3 camPos = handPos + new Vector3(0.15f, 0.2f, 0f);

			// Use the player's head direction directly — same as POV mode
			// Build the rotation from the head's forward and up vectors
			Vector3 headForward = targetPlayer.head.forward.ToVector3();
			Vector3 headUp = targetPlayer.head.up.ToVector3();
			if (headForward.LengthSquared() < 0.001f) headForward = Vector3.UnitZ;
			if (headUp.LengthSquared() < 0.001f) headUp = Vector3.UnitY;
			Quaternion camRot = CameraWriteController.QuaternionLookRotation(headForward, headUp);

			// Initialize on first frame to avoid lerping from origin
			if (!initialized)
			{
				smoothPos = camPos;
				smoothRot = camRot;
				initialized = true;
			}

			// Smooth — higher value = more responsive, lower = smoother
			float t = Math.Clamp(6f * deltaTime, 0f, 1f);
			smoothPos = Vector3.Lerp(smoothPos, camPos, t);
			smoothRot = Quaternion.Slerp(smoothRot, camRot, t);

			cameraTransform.Position = smoothPos;
			cameraTransform.Rotation = smoothRot;

			// Convert FOV to radians
			cameraTransform.fovy = SparkSettings.instance.goProWiderFov * CameraWriteController.Deg2Rad;

			return Task.CompletedTask;
		}
	}
}
