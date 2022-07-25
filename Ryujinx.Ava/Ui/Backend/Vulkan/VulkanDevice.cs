using System;
using Silk.NET.Vulkan;

namespace Ryujinx.Ava.Ui.Vulkan
{
    internal class VulkanDevice : IDisposable
    {
        private static object _lock = new object();

        public VulkanDevice(Device apiHandle, VulkanPhysicalDevice physicalDevice, Vk api)
        {
            InternalHandle = apiHandle;
            Api = api;

            // pick last queue in family
            api.GetDeviceQueue(apiHandle, physicalDevice.QueueFamilyIndex, Math.Min(1, physicalDevice.QueueCount - 1), out var queue);

            Queue = new VulkanQueue(this, queue);

            PresentQueue = Queue;

            CommandBufferPool = new VulkanCommandBufferPool(this, physicalDevice);
        }

        public IntPtr Handle => InternalHandle.Handle;

        internal Device InternalHandle { get; }
        public Vk Api { get; }

        public VulkanQueue Queue { get; private set; }
        public VulkanQueue PresentQueue { get; }
        public VulkanCommandBufferPool CommandBufferPool { get; }

        public void Dispose()
        {
            WaitIdle();
            CommandBufferPool?.Dispose();
            Queue = null;
        }

        internal void Submit(SubmitInfo submitInfo, Fence fence = new())
        {
            lock (_lock)
            {
                Api.QueueSubmit(Queue.InternalHandle, 1, submitInfo, fence).ThrowOnError();
            }
        }

        public void WaitIdle()
        { 
            lock (_lock)
            {
                Api.DeviceWaitIdle(InternalHandle);
            }
        }

        public void QueueWaitIdle()
        {
            lock (_lock)
            {
                Api.QueueWaitIdle(Queue.InternalHandle);
            }
        }

        public object Lock => _lock;
    }
}
