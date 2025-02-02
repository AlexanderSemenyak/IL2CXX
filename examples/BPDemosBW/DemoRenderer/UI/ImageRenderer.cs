﻿using BepuUtilities;
using BepuUtilities.Memory;
using DemoContentLoader;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Blazor.Extensions.Canvas.WebGL;

namespace DemoRenderer.UI
{
    /// <summary>
    /// GPU-relevant information for the rendering of a single image instance.
    /// </summary>
    public struct ImageInstance
    {
        /// <summary>
        /// Packed location of the minimum corner of the glyph. Lower 16 bits is X, upper 16 bits is Y. Should be scaled by PackedToScreen.
        /// </summary>
        public uint PackedMinimum;
        /// <summary>
        /// Packed horizontal axis used by the glyph. Lower 16 bits is X, upper 16 bits is Y. UNORM packed across a range from -1.0 at 0 to 1.0 at 65534.
        /// </summary>
        public uint PackedHorizontalAxis;
        /// <summary>
        /// Packed width and height. Width is in the lower 16 bits, height is in the upper 16 bits.
        /// </summary>
        public uint PackedSize;
        /// <summary>
        /// RGBA color, packed in a UNORM manner such that bits 0 through 7 are R, bits 8 through 15 are G, bits 16 through 23 are B, and bits 24 through 31 are A.
        /// </summary>
        public uint PackedColor;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ImageInstance(in Vector2 start, in Vector2 horizontalAxis, in Vector2 size, in Vector4 color, in Vector2 screenToPackedScale)
        {
            //Note that this can do some weird stuff if the position is outside of the target range. For the sake of the demos, we just assume everything's in frame.
            //If you want to use this for a game where you can't guarantee that everything's in frame, this packing range would need to be modified.
            //One simple option is to just set the mapped region to extend beyond the rendered target. It reduces the precision density a bit, but that's not too important.
            PackedMinimum = (uint)(start.X * screenToPackedScale.X) | ((uint)(start.Y * screenToPackedScale.Y) << 16);
            var scaledAxisX = (uint)(horizontalAxis.X * 32767f + 32767f);
            var scaledAxisY = (uint)(horizontalAxis.Y * 32767f + 32767f);
            Debug.Assert(scaledAxisX <= 65534);
            Debug.Assert(scaledAxisY <= 65534);
            PackedHorizontalAxis = scaledAxisX | (scaledAxisY << 16);
            const float sizeScale = 65535f / 4096f;
            var scaledSize = size * sizeScale;
            var clampedSize = Vector2.Max(Vector2.Zero, Vector2.Min(new Vector2(65535f), scaledSize));
            PackedSize = (uint)clampedSize.X | (((uint)clampedSize.Y) << 16);           
            PackedColor = Helpers.PackColor(color);
        }
    }

    public class ImageRenderer : Shader
    {
        struct VertexConstants
        {
            public Vector2 PackedToScreenScale;
            public Vector2 ScreenToNDCScale;
        }

        private readonly ConstantsBuffer<VertexConstants> vertexConstants;
        private readonly IndexBuffer indices;
        private readonly InstanceBuffer instances;

        public ImageRenderer(WebGL2Context context) : base(context)
        {
            vertexConstants = new(context);
            indices = new(context);
            instances = new(context);
        }
        public async Task InitializeAsync(ContentArchive content, int maximumInstancesPerDraw = 2048)
        {
            await InitializeAsync(
                content.Load<GLSLContent>(@"UI\RenderImages.glvs").Source,
                content.Load<GLSLContent>(@"UI\RenderImages.glfs").Source
            );
            await context.UniformBlockBindingAsync(program, await context.GetUniformBlockIndexAsync(program, "type_VertexConstants"), 0);
            await vertexConstants.InitializeAsync();
            await indices.InitializeAsync(Helpers.GetQuadIndices(1));
            await instances.InitializeAsync<ImageInstance>(maximumInstancesPerDraw);
            await instances.VertexAttribIPointerAsync(0, 1, 0);
            await instances.VertexAttribIPointerAsync(1, 1, sizeof(uint));
            await instances.VertexAttribIPointerAsync(2, 1, sizeof(uint) * 2);
            await instances.VertexAttribIPointerAsync(3, 1, sizeof(uint) * 3);
        }
        protected override async ValueTask DoDisposeAsync()
        {
            await vertexConstants.DisposeAsync();
            await indices.DisposeAsync();
            await instances.DisposeAsync();
            await base.DoDisposeAsync();
        }
        /// <summary>
        /// Sets up the rendering pipeline with any glyph rendering specific render state that can be shared across all glyph batches drawn using the GlyphRenderer.Render function.
        /// </summary>
        public Task PreparePipelineAsync() => UseAsync();
        public async Task RenderAsync(RenderableImage image, Int2 screenResolution, Buffer<ImageInstance> instances)
        {
            await context.BindTextureAsync(TextureType.TEXTURE_2D, image.Texture);
            await vertexConstants.UpdateAsync(0, new()
            {
                //These first two scales could be uploaded once, but it would require another buffer. Not important enough.
                //The packed minimum must permit subpixel locations. So, distribute the range 0 to 65535 over the pixel range 0 to resolution.
                PackedToScreenScale = new(screenResolution.X / 65535f, screenResolution.Y / 65535f),
                ScreenToNDCScale = new(2f / screenResolution.X, -2f / screenResolution.Y)
            });
            var count = instances.Length;
            var start = 0;
            while (count > 0)
            {
                var batchCount = Math.Min(this.instances.Capacity, count);
                await this.instances.UpdateAsync(instances.Slice(start, batchCount).ToArray());
                await context.DrawElementsInstancedAsync(Primitive.TRIANGLES, 6, indices.Type, 0, batchCount);
                count -= batchCount;
                start += batchCount;
            }
        }
    }
}
