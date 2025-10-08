using System;
using System.Collections.Generic;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Aetheris
{
    /// <summary>
    /// Manages networked player movement with client-side prediction
    /// </summary>
    public class PlayerNetworkController
    {
        private readonly Player player;
        private readonly Client client;
        
        // Network timing
        private float udpSendTimer = 0f;
        private const float UDP_SEND_RATE = 1f / 30f; // 30 Hz
        
        // Client-side prediction
        private readonly Queue<PlayerInputState> inputHistory = new();
        private const int MAX_INPUT_HISTORY = 64;
        private uint currentInputSequence = 0;
        
        // Server reconciliation
        private uint lastAcknowledgedInput = 0;
        private float timeSinceLastServerUpdate = 0f;
        private const float SERVER_TIMEOUT = 5f;
        
        // Remote players (other players in the world)
        private readonly Dictionary<string, RemotePlayer> remotePlayers = new();
        
        public IReadOnlyDictionary<string, RemotePlayer> RemotePlayers => remotePlayers;
        
        public PlayerNetworkController(Player player, Client client)
        {
            this.player = player;
            this.client = client;
        }
        
        public void Update(FrameEventArgs e, KeyboardState keys, MouseState mouse)
        {
            if (player == null)
            {
                Console.WriteLine("[Network] Error: Player is null");
                return;
            }
            
            float deltaTime = (float)e.Time;
            
            // Client-side prediction: run physics locally FIRST
            player.Update(e, keys, mouse);
            
            // Create input state for this frame (after update so we have current state)
            var inputState = new PlayerInputState
            {
                Sequence = currentInputSequence++,
                DeltaTime = deltaTime,
                Forward = keys.IsKeyDown(Keys.W),
                Backward = keys.IsKeyDown(Keys.S),
                Left = keys.IsKeyDown(Keys.A),
                Right = keys.IsKeyDown(Keys.D),
                Jump = keys.IsKeyDown(Keys.Space),
                Yaw = player.Yaw,
                Pitch = player.Pitch
            };
            
            // Store input for reconciliation
            inputHistory.Enqueue(inputState);
            while (inputHistory.Count > MAX_INPUT_HISTORY)
                inputHistory.Dequeue();
            
            // Send updates to server
            udpSendTimer += deltaTime;
            timeSinceLastServerUpdate += deltaTime;
            
            if (udpSendTimer >= UDP_SEND_RATE)
            {
                SendPlayerUpdate(inputState);
                udpSendTimer = 0f;
            }
            
            // Warn about server timeout
            if (timeSinceLastServerUpdate > SERVER_TIMEOUT)
            {
                Console.WriteLine($"[Network] Warning: No server update for {timeSinceLastServerUpdate:F1}s");
            }
            
            // Update remote players (smooth interpolation)
            UpdateRemotePlayers(deltaTime);
        }
        
        private void SendPlayerUpdate(PlayerInputState input)
        {
            // Packet: PlayerPosition (type 1)
            byte[] packet = new byte[38];
            packet[0] = 1; // PlayerPosition
            
            BitConverter.TryWriteBytes(packet.AsSpan(1, 4), input.Sequence);
            BitConverter.TryWriteBytes(packet.AsSpan(5, 4), player.Position.X);
            BitConverter.TryWriteBytes(packet.AsSpan(9, 4), player.Position.Y);
            BitConverter.TryWriteBytes(packet.AsSpan(13, 4), player.Position.Z);
            BitConverter.TryWriteBytes(packet.AsSpan(17, 4), player.Velocity.X);
            BitConverter.TryWriteBytes(packet.AsSpan(21, 4), player.Velocity.Y);
            BitConverter.TryWriteBytes(packet.AsSpan(25, 4), player.Velocity.Z);
            BitConverter.TryWriteBytes(packet.AsSpan(29, 4), input.Yaw);
            BitConverter.TryWriteBytes(packet.AsSpan(33, 4), input.Pitch);
            
            // Input flags
            byte flags = 0;
            if (input.Forward) flags |= 0x01;
            if (input.Backward) flags |= 0x02;
            if (input.Left) flags |= 0x04;
            if (input.Right) flags |= 0x08;
            if (input.Jump) flags |= 0x10;
            if (player.IsGrounded) flags |= 0x20;
            packet[37] = flags;
            
            _ = client.SendUdpAsync(packet);
        }
        
        /// <summary>
        /// Called when server acknowledges our position
        /// </summary>
        public void OnServerUpdate(ServerPlayerUpdate update)
        {
            timeSinceLastServerUpdate = 0f;
            lastAcknowledgedInput = update.AcknowledgedSequence;
            
            // Calculate prediction error
            float posError = Vector3.Distance(player.Position, update.Position);
            
            // Only reconcile if error is significant (> 10cm)
            if (posError > 0.1f)
            {
                Console.WriteLine($"[Prediction] Error: {posError * 100:F1}cm - reconciling");
                ReconcilePosition(update);
            }
            else
            {
                // Small errors - just clean up old inputs
                CleanupOldInputs(update.AcknowledgedSequence);
            }
        }
        
        private void ReconcilePosition(ServerPlayerUpdate update)
        {
            // Reset to server's authoritative state
            player.SetPosition(update.Position);
            player.SetVelocity(update.Velocity);
            player.SetRotation(update.Yaw, update.Pitch);
            
            // Collect inputs to replay
            var toReplay = new List<PlayerInputState>();
            
            foreach (var input in inputHistory)
            {
                if (input.Sequence > update.AcknowledgedSequence)
                {
                    toReplay.Add(input);
                }
            }
            
            // Replay unacknowledged inputs
            foreach (var input in toReplay)
            {
                player.SimulateInput(input);
            }
            
            // Clean up acknowledged inputs
            CleanupOldInputs(update.AcknowledgedSequence);
            
            if (toReplay.Count > 0)
            {
                Console.WriteLine($"[Prediction] Replayed {toReplay.Count} inputs");
            }
        }
        
        private void CleanupOldInputs(uint acknowledgedSequence)
        {
            while (inputHistory.Count > 0 && inputHistory.Peek().Sequence <= acknowledgedSequence)
            {
                inputHistory.Dequeue();
            }
        }
        
        /// <summary>
        /// Called when receiving another player's state
        /// </summary>
        public void OnRemotePlayerUpdate(string playerId, ServerPlayerUpdate update)
        {
            if (!remotePlayers.TryGetValue(playerId, out var remote))
            {
                remote = new RemotePlayer(playerId, update.Position);
                remotePlayers[playerId] = remote;
                Console.WriteLine($"[Network] New player joined: {playerId}");
            }
            
            remote.UpdateFromServer(
                update.Position,
                update.Velocity,
                update.Yaw,
                update.Pitch,
                false // isGrounded not in packet yet
            );
        }
        
        private void UpdateRemotePlayers(float deltaTime)
        {
            foreach (var remote in remotePlayers.Values)
            {
                remote.Update(deltaTime);
            }
        }
    }
    
    // ============================================================================
    // Data Structures
    // ============================================================================
    
    public struct PlayerInputState
    {
        public uint Sequence;
        public float DeltaTime;
        public bool Forward;
        public bool Backward;
        public bool Left;
        public bool Right;
        public bool Jump;
        public float Yaw;
        public float Pitch;
    }
    
    public struct ServerPlayerUpdate
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Yaw;
        public float Pitch;
        public uint AcknowledgedSequence;
        public long Timestamp;
    }
}
