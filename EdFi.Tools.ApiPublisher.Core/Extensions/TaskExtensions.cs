using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.Extensions
{
    public static class TaskExtensions
    {
        /// <summary>
        /// Gets the result of a asynchronous Task configuring the await to not try to continue
        /// on the captured context, thereby avoiding deadlocks. 
        /// </summary>
        /// <param name="task">The asynchronous task to be synchronously completed.</param>
        /// <typeparam name="T">The return type of the Task.</typeparam>
        /// <returns>The task's result.</returns>
        public static T GetResultSafely<T>(this Task<T> task)
        {
            return task.ConfigureAwait(false).GetAwaiter().GetResult();
        }
    }
}