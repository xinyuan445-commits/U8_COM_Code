using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MaterialOutboundApi.Infrastructure;

namespace MaterialOutboundApi.Services
{
    internal sealed class U8OperationQueue : IDisposable
    {
        private const string MutexName =
            @"Global\MaterialOutboundApi.U8Operations";
        private readonly BlockingCollection<IWorkItem> _queue =
            new BlockingCollection<IWorkItem>(50);
        private readonly Thread _thread;
        private bool _disposed;

        public U8OperationQueue()
        {
            _thread = new Thread(Work)
            {
                IsBackground = true,
                Name = "MaterialOutboundApi.U8.STA"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public Task<T> Execute<T>(Func<T> action)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(U8OperationQueue));
            }
            var item = new WorkItem<T>(action, U8DatabaseContext.Current);
            if (!_queue.TryAdd(item))
            {
                throw new ApiException(
                    HttpStatusCode.ServiceUnavailable,
                    "U8_QUEUE_FULL",
                    "U8 操作队列已满，请稍后重试。");
            }
            return item.Task;
        }

        private void Work()
        {
            foreach (IWorkItem item in _queue.GetConsumingEnumerable())
            {
                using (var mutex = new Mutex(false, MutexName))
                {
                    bool acquired = false;
                    try
                    {
                        try
                        {
                            acquired = mutex.WaitOne(TimeSpan.FromSeconds(120));
                        }
                        catch (AbandonedMutexException)
                        {
                            acquired = true;
                        }
                        if (!acquired)
                        {
                            throw new ApiException(
                                HttpStatusCode.ServiceUnavailable,
                                "U8_BUSY",
                                "其他 U8 操作仍在执行，请稍后重试。");
                        }
                        item.Run();
                    }
                    catch (Exception exception)
                    {
                        item.Fail(exception);
                    }
                    finally
                    {
                        if (acquired)
                        {
                            try
                            {
                                mutex.ReleaseMutex();
                            }
                            catch
                            {
                                // 进程回收阶段忽略释放异常。
                            }
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(30));
            _queue.Dispose();
        }

        private interface IWorkItem
        {
            void Run();
            void Fail(Exception exception);
        }

        private sealed class WorkItem<T> : IWorkItem
        {
            private readonly Func<T> _action;
            private readonly string _u8Database;
            private readonly TaskCompletionSource<T> _completion =
                new TaskCompletionSource<T>();

            public WorkItem(Func<T> action, string u8Database)
            {
                _action = action;
                _u8Database = u8Database;
            }

            public Task<T> Task => _completion.Task;

            public void Run()
            {
                string previous = U8DatabaseContext.Current;
                U8DatabaseContext.Current = _u8Database;
                try
                {
                    _completion.SetResult(_action());
                }
                catch (Exception exception)
                {
                    _completion.SetException(exception);
                }
                finally
                {
                    U8DatabaseContext.Current = previous;
                }
            }

            public void Fail(Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}
