// Copyright (c) 2004-2011 Azavea, Inc.
// 
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.

namespace Azavea.Open.DAO.Exceptions
{
    /// <summary>
    /// This exception is thrown when trying to use a transaction with a connection
    /// that doesn't support them.
    /// </summary>
    public class TransactionsNotSupportedException : ExceptionWithConnectionInfo
    {
        /// <summary>
        /// This connection doesn't support transactions.
        /// </summary>
        /// <param name="desc">Connection descriptor we were using to try to connect.</param>
        /// <param name="message">A message describing what is wrong with the connection type.</param>
        public TransactionsNotSupportedException(IConnectionDescriptor desc, string message)
            : base(message, desc, null)
        {
            // Nothing to set here.
        }
    }
}