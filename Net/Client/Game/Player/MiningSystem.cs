using System;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Aetheris
{
    /// <summary>
    /// Mining system with raycasting and block hardness
    /// </summary>
    public class MiningSystem
    {
        private readonly Player player;
        private readonly RaycastHelper raycaster;
        private readonly Action<Vector3, BlockType> onBlockMined;
        
        // Mining state
        private Vector3? currentTarget = null;
        private float miningProgress = 0f;
        private BlockType targetBlockType = BlockType.Air;
        
        // Mining config
        private const float MAX_REACH = 5f; // blocks
        private const float MINING_SPEED_MULT = 1f; // Global speed multiplier
        
        // Block hardness (seconds to mine)
        private static readonly float[] BlockHardness = new float[]
        {
            0f,    // Air (instant)
            2f,    // Stone
            0.8f,  // Dirt
            0.8f,  // Grass
            0.5f,  // Sand
            1.5f,  // Snow
            1.2f,  // Gravel
            1.5f,  // Wood
            0.3f   // Leaves
        };
        
        public MiningSystem(Player player, Game game, Action<Vector3, BlockType> onBlockMined)
        {
            this.player = player;
            this.raycaster = new RaycastHelper(game);
            this.onBlockMined = onBlockMined;
        }
        
        /// <summary>
        /// Call this every frame with mouse state
        /// </summary>
        public void Update(float deltaTime, MouseState mouse, bool isWindowFocused)
        {
            if (!isWindowFocused) return;
            
            // Perform raycast to find what we're looking at
            Vector3 rayStart = player.Position + Vector3.UnitY * 2.6f; // Eye height
            Vector3 rayDir = player.GetForward();
            Vector3 rayEnd = rayStart + rayDir * MAX_REACH;
            
            var hits = raycaster.Raycast(rayStart, rayEnd, raycastAll: false);
            
            // Check if we hit something
            if (hits.Length > 0 && hits[0].Hit)
            {
                var hit = hits[0];
                Vector3 hitPos = hit.Point - hit.Normal * 0.1f; // Slightly inside the block
                Vector3 blockPos = new Vector3(
                    MathF.Floor(hitPos.X),
                    MathF.Floor(hitPos.Y),
                    MathF.Floor(hitPos.Z)
                );
                
                // Check if we're mining (left click held)
                if (mouse.IsButtonDown(MouseButton.Left))
                {
                    // Same block as before?
                    if (currentTarget.HasValue && Vector3.Distance(currentTarget.Value, blockPos) < 0.1f)
                    {
                        // Continue mining
                        float hardness = GetBlockHardness(targetBlockType);
                        miningProgress += deltaTime / (hardness * MINING_SPEED_MULT);
                        
                        if (miningProgress >= 1f)
                        {
                            // Block mined!
                            onBlockMined?.Invoke(blockPos, targetBlockType);
                            ResetMining();
                        }
                    }
                    else
                    {
                        // Start mining new block
                        currentTarget = blockPos;
                        targetBlockType = hit.BlockType;
                        miningProgress = 0f;
                    }
                }
                else
                {
                    // Not mining anymore
                    ResetMining();
                }
            }
            else
            {
                // Not looking at anything
                ResetMining();
            }
        }
        
        /// <summary>
        /// Get how long it takes to mine a block (in seconds)
        /// </summary>
        private float GetBlockHardness(BlockType blockType)
        {
            int index = (int)blockType;
            if (index < 0 || index >= BlockHardness.Length)
                return 1f;
            return BlockHardness[index];
        }
        
        private void ResetMining()
        {
            currentTarget = null;
            miningProgress = 0f;
            targetBlockType = BlockType.Air;
        }
        
        /// <summary>
        /// Get current mining info for UI display
        /// </summary>
        public (bool isMining, BlockType blockType, float progress, Vector3? position) GetMiningInfo()
        {
            return (currentTarget.HasValue, targetBlockType, miningProgress, currentTarget);
        }
        
        /// <summary>
        /// Get what the player is currently looking at (for crosshair/UI)
        /// </summary>
        public (bool lookingAt, BlockType blockType, Vector3 position, float distance) GetLookTarget()
        {
            Vector3 rayStart = player.Position + Vector3.UnitY * 2.6f;
            Vector3 rayDir = player.GetForward();
            Vector3 rayEnd = rayStart + rayDir * MAX_REACH;
            
            var hits = raycaster.Raycast(rayStart, rayEnd, raycastAll: false);
            
            if (hits.Length > 0 && hits[0].Hit)
            {
                var hit = hits[0];
                Vector3 hitPos = hit.Point - hit.Normal * 0.1f;
                Vector3 blockPos = new Vector3(
                    MathF.Floor(hitPos.X),
                    MathF.Floor(hitPos.Y),
                    MathF.Floor(hitPos.Z)
                );
                
                return (true, hit.BlockType, blockPos, hit.Distance);
            }
            
            return (false, BlockType.Air, Vector3.Zero, 0f);
        }
    }
}
