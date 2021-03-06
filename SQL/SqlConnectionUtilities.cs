// Copyright (c) 2004-2010 Azavea, Inc.
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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;
using Azavea.Open.DAO.Exceptions;
using Azavea.Open.DAO.Util;
using log4net;

namespace Azavea.Open.DAO.SQL
{
    /// <summary>
    /// This class holds static utility methods having to do with database access.  The goal is
    /// twofold:
    ///   1: To reduce the number of lines of code required to to basic database stuff (if you're
    ///      doing more complicated stuff, it may be appropriate to look at Azavea.Common.NHibernate
    ///      and consider using NHibernate).
    ///   2: To ensure that connections are handled appropriately even in exception cases (I.E. they
    ///      are closed and/or returned to the pool whether or not exceptions happen).
    /// 
    /// Since no one likes obnoxiously long method names, "ExceptionSafe" has been abbreviated
    /// as "XSafe".
    /// 
    /// There are two groups of methods, the "XSafe" query/command methods and the "DataSet" methods.
    /// The DataSet methods are provided primarily because some things (such as .NET controls) use DataSets directly.
    /// 
    /// However, in general when writing new code you should use the XSafe methods, because they use
    /// DataReaders which are a faster way of accessing the database than DataSets.
    /// 
    /// This class currently implements fairly primitive connection pooling.  While .NET claims
    /// to do connection pooling for you, there is still a significant performance savings to be
    /// had by saving "DbConnection" objects and reusing them.  The existing connection pooling
    /// basically saves one connection to any database you use this class to connect to.  This
    /// achieves most of the savings that can be achieved with pooling, but if necessary a more
    /// complicated pooling scheme can be added in the future.
    /// Note that connection pooling is NOT used for all databases, since they may not
    /// support more than one "write" connection at a time.
    /// </summary>
    public static class SqlConnectionUtilities
    {
        private readonly static ILog _log = LogManager.GetLogger(
            new System.Diagnostics.StackTrace().GetFrame(0).GetMethod().DeclaringType.Namespace);

        #region Public utility methods.

        /// <summary>
        /// This provides a way to query the DB and do something with the results without
        /// having to copy the "try, open, try, execute, finally, close, finally, close"
        /// type logic in a bunch of places.  This method correctly closes the objects
        /// used in the DB access in the event of an exception.
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="sql">The SQL statement to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <param name="invokeMe">The method to delegate to.  If null, nothing is
        ///                        done with the data reader and it is just closed.</param>
        /// <param name="parameters">The other parameters to the delegate, in whatever
        ///							 form makes sense for that delegate method.</param>
        public static void XSafeQuery(AbstractSqlConnectionDescriptor connDesc, string sql,
                                      IEnumerable sqlParams, DataReaderDelegate invokeMe, Hashtable parameters)
        {
            XSafeQuery(connDesc, null, sql, sqlParams, invokeMe, parameters);
        }
        /// <summary>
        /// This provides a way to query the DB and do something with the results without
        /// having to copy the "try, open, try, execute, finally, close, finally, close"
        /// type logic in a bunch of places.  This method correctly closes the objects
        /// used in the DB access in the event of an exception.
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="transaction">The transaction to do this as part of.</param>
        /// <param name="sql">The SQL statement to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <param name="invokeMe">The method to delegate to.  If null, nothing is
        ///                        done with the data reader and it is just closed.</param>
        /// <param name="parameters">The other parameters to the delegate, in whatever
        ///							 form makes sense for that delegate method.</param>
        public static void XSafeQuery(AbstractSqlConnectionDescriptor connDesc,
            SqlTransaction transaction, string sql,
            IEnumerable sqlParams, DataReaderDelegate invokeMe, Hashtable parameters) 
        {
            IDbConnection conn = transaction != null
                ? transaction.Connection
                : DbCaches.Connections.Get(connDesc);
            try 
            {
                IDbCommand cmd = DbCaches.Commands.Get(sql, conn);
                if (transaction != null)
                {
                    cmd.Transaction = transaction.Transaction;
                }
                try 
                {
                    SetSQLOnCommand(connDesc, cmd, sql, sqlParams);
                    IDataReader reader;
                    try 
                    {
                        reader = cmd.ExecuteReader();
                    } 
                    catch (Exception e) 
                    {
                        throw new UnableToRunSqlException(connDesc, sql, sqlParams, e);
                    }
                    try 
                    {
                        if (invokeMe != null) 
                        {
                            invokeMe(parameters, reader);
                        }
                    }
                    catch (ExceptionWithConnectionInfo)
                    {
                        // The delegate may have thrown an exception that came from another
                        // database utilities call, in which case we don't want to confuse
                        // the issue by wrapping with a different SQL command.
                        throw;
                    }
                    catch (Exception e) 
                    {
                        throw new UnableToProcessSqlResultsException(connDesc, sql, sqlParams, e);
                    }
                    finally 
                    {
                        try 
                        {
                            reader.Close();
                        } 
                        catch (Exception e) 
                        {
                            // This exception is not rethrown because we don't want to mask any
                            // previous exception that might be being thrown out of "invokeMe".
                            _log.Warn("Caught an exception while trying to close the data reader.", e);
                        }
                    }
                } 
                finally 
                {
                    DbCaches.Commands.Return(sql, conn, cmd);
                }
            }
            finally 
            {
                if (transaction == null)
                {
                    DbCaches.Connections.Return(connDesc, conn);
                }
            }
        }

        /// <summary>
        /// Similar to the "XSafeQuery" method, except this executes a
        /// query that returns a single string.
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="sql">The SQL query to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <returns>The string returned by the query.</returns>
        public static string XSafeStringQuery(AbstractSqlConnectionDescriptor connDesc, string sql,
                                              IEnumerable sqlParams)
        {
            return XSafeStringQuery(connDesc, null, sql, sqlParams);
        }
        /// <summary>
        /// Similar to the "XSafeQuery" method, except this executes a
        /// query that returns a single string.
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="transaction">The transaction to do this as part of.</param>
        /// <param name="sql">The SQL query to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <returns>The string returned by the query.</returns>
        public static string XSafeStringQuery(AbstractSqlConnectionDescriptor connDesc,
            SqlTransaction transaction, string sql, IEnumerable sqlParams)
        {
            string retVal = null;
            object retObj = XSafeScalarQuery(connDesc, transaction, sql, sqlParams);
            try
            {
                // If it's null, retVal will stay null.
                if (retObj != null)
                {
                    retVal = retObj.ToString();
                }
            }
            catch (Exception e)
            {
                throw new UnableToProcessSqlResultsException("Result was not ToStringable. ",
                                                             connDesc, sql, sqlParams, e);
            }
            return retVal;
        }

        /// <summary>
        /// Similar to the "XSafeQuery" method, except this executes a
        /// query that returns a single integer (such as SELECT COUNT).
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="sql">The SQL query to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <returns>The integer returned by the query.</returns>
        public static int XSafeIntQuery(AbstractSqlConnectionDescriptor connDesc, string sql,
                                        IEnumerable sqlParams)
        {
            return XSafeIntQuery(connDesc, null, sql, sqlParams);
        }

        /// <summary>
        /// Similar to the "XSafeQuery" method, except this executes a
        /// query that returns a single integer (such as SELECT COUNT).
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="transaction">The transaction to do this as part of.</param>
        /// <param name="sql">The SQL query to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <returns>The integer returned by the query.</returns>
        public static int XSafeIntQuery(AbstractSqlConnectionDescriptor connDesc,
            SqlTransaction transaction, string sql, IEnumerable sqlParams)
        {
            int retVal;
            object retObj = XSafeScalarQuery(connDesc, transaction, sql, sqlParams);
            try
            {
                if (retObj == null)
                {
                    throw new NullReferenceException(
                        "The sql query should have returned an int, but returned null instead.");
                }
                retVal = Convert.ToInt32(retObj);
            }
            catch (Exception e)
            {
                throw new UnableToProcessSqlResultsException("Result was not numeric. ",
                                                             connDesc, sql, sqlParams, e);
            }
            return retVal;
        }

        /// <summary>
        /// Similar to the "XSafeQuery" method, except this executes a
        /// query that returns a single boolean.
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="sql">The SQL query to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <returns>The boolean returned by the query.</returns>
        public static bool XSafeBoolQuery(AbstractSqlConnectionDescriptor connDesc, string sql,
                                        IEnumerable sqlParams)
        {
            return XSafeBoolQuery(connDesc, null, sql, sqlParams);
        }

        /// <summary>
        /// Similar to the "XSafeQuery" method, except this executes a
        /// query that returns a single boolean.
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="transaction">The transaction to do this as part of.</param>
        /// <param name="sql">The SQL query to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <returns>The boolean returned by the query.</returns>
        public static bool XSafeBoolQuery(AbstractSqlConnectionDescriptor connDesc,
            SqlTransaction transaction, string sql, IEnumerable sqlParams)
        {
            bool retVal;
            object retObj = XSafeScalarQuery(connDesc, transaction, sql, sqlParams);
            try
            {
                if (retObj == null)
                {
                    throw new NullReferenceException(
                        "The sql query should have returned a boolean, but returned null instead.");
                }
                retVal = Convert.ToBoolean(retObj);
            }
            catch (Exception e)
            {
                throw new UnableToProcessSqlResultsException("Result was not boolean. ",
                                                             connDesc, sql, sqlParams, e);
            }
            return retVal;
        }
        /// <summary>
        /// Similar to the "XSafeQuery" method, except this executes a
        /// query that returns a single DateTime (such as SELECT MAX(DateField)).
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="sql">The SQL query to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <returns>The DateTime returned by the query.</returns>
        public static DateTime XSafeDateQuery(AbstractSqlConnectionDescriptor connDesc,
            string sql, IEnumerable sqlParams)
        {
            return XSafeDateQuery(connDesc, null, sql, sqlParams);
        }
        /// <summary>
        /// Similar to the "XSafeQuery" method, except this executes a
        /// query that returns a single DateTime (such as SELECT MAX(DateField)).
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="transaction">The transaction to do this as part of.</param>
        /// <param name="sql">The SQL query to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <returns>The DateTime returned by the query.</returns>
        public static DateTime XSafeDateQuery(AbstractSqlConnectionDescriptor connDesc,
            SqlTransaction transaction, string sql, IEnumerable sqlParams)
        {
            DateTime retVal;
            object retObj = XSafeScalarQuery(connDesc, transaction, sql, sqlParams);
            try
            {
                if (retObj == null)
                {
                    throw new NullReferenceException(
                        "The sql query should have returned an int, but returned null instead.");
                }
                retVal = Convert.ToDateTime(retObj);
            }
            catch (Exception e)
            {
                throw new UnableToProcessSqlResultsException("Result was not date/time. ",
                                                             connDesc, sql, sqlParams, e);
            }
            return retVal;
        }

        /// <summary>
        /// Similar to the "XSafeQuery" method, except this executes a
        /// query that returns a single floating-point number (such as SELECT SUM(...)).
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="sql">The SQL query to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <returns>The floating-point number returned by the query.</returns>
        public static double XSafeDoubleQuery(AbstractSqlConnectionDescriptor connDesc, string sql,
                                              IEnumerable sqlParams)
        {
            return XSafeDoubleQuery(connDesc, null, sql, sqlParams);
        }
        /// <summary>
        /// Similar to the "XSafeQuery" method, except this executes a
        /// query that returns a single floating-point number (such as SELECT SUM(...)).
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="transaction">The transaction to do this as part of.</param>
        /// <param name="sql">The SQL query to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <returns>The floating-point number returned by the query.</returns>
        public static double XSafeDoubleQuery(AbstractSqlConnectionDescriptor connDesc,
            SqlTransaction transaction, string sql, IEnumerable sqlParams)
        {
            double retVal;
            object retObj = XSafeScalarQuery(connDesc, transaction, sql, sqlParams);
            try
            {
                if (retObj == null)
                {
                    throw new NullReferenceException(
                        "The sql query should have returned a double, but returned null instead.");
                }
                retVal = Convert.ToDouble(retObj);
            }
            catch (Exception e)
            {
                throw new UnableToProcessSqlResultsException("Result was not numeric. ",
                                                             connDesc, sql, sqlParams, e);
            }
            return retVal;
        }

        /// <summary>
        /// Similar to the other XSafe methods, except this one returns a list of
        /// strings (for example, SELECT NAME FROM EMPLOYEES).
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="sql">The SQL query to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <returns>A List of strings, from the first column of the results 
        ///			(presumably there is only one column in the results).  If no results
        ///			were returned, this List will be empty.</returns>
        public static IList<string> XSafeStringListQuery(AbstractSqlConnectionDescriptor connDesc, string sql,
                                                         IEnumerable sqlParams)
        {
            return XSafeStringListQuery(connDesc, null, sql, sqlParams);
        }
        /// <summary>
        /// Similar to the other XSafe methods, except this one returns a list of
        /// strings (for example, SELECT NAME FROM EMPLOYEES).
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="transaction">The transaction to do this as part of.</param>
        /// <param name="sql">The SQL query to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <returns>A List of strings, from the first column of the results 
        ///			(presumably there is only one column in the results).  If no results
        ///			were returned, this List will be empty.</returns>
        public static IList<string> XSafeStringListQuery(AbstractSqlConnectionDescriptor connDesc,
            SqlTransaction transaction, string sql, IEnumerable sqlParams) 
        {
            Hashtable parameters = DbCaches.Hashtables.Get();
            XSafeQuery(connDesc, transaction, sql, sqlParams, ReadStringsFromQuery, parameters);
            IList<string> retVal = (IList<string>)parameters["results"];
            DbCaches.Hashtables.Return(parameters);
            return retVal;
        }

        /// <summary>
        /// Similar to the other XSafe methods, except this one returns a list of
        /// integers (for example, SELECT AGE FROM EMPLOYEES).
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="sql">The SQL query to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <returns>A List of integers, from the first column of the results 
        ///			(presumably there is only one column in the results).  If no results
        ///			were returned, this List will be empty.  Since integers are not nullable,
        ///         NULL VALUES WILL BE IGNORED.</returns>
        public static IList<int> XSafeIntListQuery(AbstractSqlConnectionDescriptor connDesc, string sql,
                                                   IEnumerable sqlParams)
        {
            return XSafeIntListQuery(connDesc, null, sql, sqlParams);
        }
        /// <summary>
        /// Similar to the other XSafe methods, except this one returns a list of
        /// integers (for example, SELECT AGE FROM EMPLOYEES).
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="transaction">The transaction to do this as part of.</param>
        /// <param name="sql">The SQL query to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <returns>A List of integers, from the first column of the results 
        ///			(presumably there is only one column in the results).  If no results
        ///			were returned, this List will be empty.  Since integers are not nullable,
        ///         NULL VALUES WILL BE IGNORED.</returns>
        public static IList<int> XSafeIntListQuery(AbstractSqlConnectionDescriptor connDesc,
            SqlTransaction transaction, string sql, IEnumerable sqlParams)
        {
            Hashtable parameters = DbCaches.Hashtables.Get();
            XSafeQuery(connDesc, transaction, sql, sqlParams, ReadIntsFromQuery, parameters);
            IList<int> retVal = (IList<int>)parameters["results"];
            DbCaches.Hashtables.Return(parameters);
            return retVal;
        }

        /// <summary>
        /// Similar to the "XSafeQuery" method, except this executes a
        /// non-query type SQL statement.
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="sql">The SQL statement to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <returns>The number of rows affected.</returns>
        public static int XSafeCommand(AbstractSqlConnectionDescriptor connDesc, string sql,
                                       IEnumerable sqlParams)
        {
            return XSafeCommand(connDesc, null, sql, sqlParams);
        }
        /// <summary>
        /// Similar to the "XSafeQuery" method, except this executes a
        /// non-query type SQL statement.
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="transaction">The transaction to do this as part of.</param>
        /// <param name="sql">The SQL statement to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <returns>The number of rows affected.</returns>
        public static int XSafeCommand(AbstractSqlConnectionDescriptor connDesc,
            SqlTransaction transaction, string sql, IEnumerable sqlParams) 
        {
            int retVal;
            IDbConnection conn = transaction != null
                ? transaction.Connection
                : DbCaches.Connections.Get(connDesc);
            try 
            {
                IDbCommand cmd = DbCaches.Commands.Get(sql, conn);
                if (transaction != null)
                {
                    cmd.Transaction = transaction.Transaction;
                }
                try 
                {
                    SetSQLOnCommand(connDesc, cmd, sql, sqlParams);
                    try 
                    {
                        retVal = cmd.ExecuteNonQuery();
                    } 
                    catch (Exception e) 
                    {
                        throw new UnableToRunSqlException(connDesc, sql, sqlParams, e);
                    }
                } 
                finally 
                {
                    DbCaches.Commands.Return(sql, conn, cmd);
                }
            } 
            finally 
            {
                if (transaction == null)
                {
                    DbCaches.Connections.Return(connDesc, conn);
                }
            }
            return retVal;
        }

        /// <summary>
        /// Queries for data and populates the specified dataset.  If you already have a dataset,
        /// use the QueryIntoDataSet method.
        /// </summary>
        /// <param name="dataTableName">The name of the DataTable to put data into in the DataSet.
        ///                         May be null if you wish to use the default given by the DataAdapter.</param>
        /// <param name="sql">The parameterized SQL query (?'s for values).</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <param name="connDesc">The database connection descriptor.  This will be
        ///                        used to get the database connection.</param>
        /// <returns>A DataSet full of data.  Should never return null, but
        ///			 may return an empty dataset.</returns>
        public static DataSet QueryForDataSet(AbstractSqlConnectionDescriptor connDesc, string dataTableName, string sql,
                                              IEnumerable sqlParams)
        {
            DataSet retVal = new DataSet();
            QueryIntoDataSet(connDesc, dataTableName, sql, sqlParams, retVal);
            return retVal;
        }

        /// <summary>
        /// Queries for data and populates the specified dataset.  If you do not have a dataset
        /// already, call QueryForDataSet instead.
        /// </summary>
        /// <param name="dataTableName">The name of the DataTable to put data into in the DataSet.
        ///                         May be null if you wish to use the default given by the DataAdapter.</param>
        /// <param name="sql">The parameterized SQL query (?'s for values).</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <param name="connDesc">The database connection descriptor.  This will be
        ///                        used to get the database connection.</param>
        /// <param name="fillMe">The dataset to put data into.  Must not be null.</param>
        public static void QueryIntoDataSet(AbstractSqlConnectionDescriptor connDesc, string dataTableName, string sql,
                                            IEnumerable sqlParams, 
                                            DataSet fillMe) 
        {
            IDbConnection conn = DbCaches.Connections.Get(connDesc);
            try
            {
                IDbCommand cmd = DbCaches.Commands.Get(sql, conn);
                try
                {
                    SetSQLOnCommand(connDesc, cmd, sql, sqlParams);
                    DbDataAdapter adapter = connDesc.CreateNewAdapter(cmd);
                    if (dataTableName != null)
                    {
                        adapter.Fill(fillMe, dataTableName);
                    }
                    else
                    {
                        adapter.Fill(fillMe);
                    }
                }
                catch (Exception e)
                {
                    throw new UnableToRunSqlException("Unable to fill dataset with query: ",
                                                      connDesc, sql, sqlParams, e);
                }
                finally
                {
                    DbCaches.Commands.Return(sql, conn, cmd);
                }
            }
            finally
            {
                DbCaches.Connections.Return(connDesc, conn);
            }
        }

        /// <summary>
        /// Attempts to determine if a connection can be made using the specified connection string.
        /// </summary>
        /// <param name="connDesc">Connection descriptor to test.</param>
        /// <returns>True if a connection could be made, false otherwise
        ///          (exceptions are logged at debug level).</returns>
        public static bool TestConnection(AbstractSqlConnectionDescriptor connDesc)
        {
            bool retVal = false;
            try
            {
                IDbConnection conn = DbCaches.Connections.Get(connDesc);
                if (conn != null)
                {
                    DbCaches.Connections.Return(connDesc, conn);
                    retVal = true;
                }
            }
            catch (Exception e)
            {
                _log.Debug("Database connection '" + connDesc + "' failed.", e);
            }
            return retVal;
        }

        /// <summary>
        /// Drops the specified index.
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="indexName">The name of the index to remove.</param>
        /// <returns>True if successful, false otherwise.  Any exceptions will be
        ///          logged as warnings.</returns>
        public static bool DropIndex(AbstractSqlConnectionDescriptor connDesc, string indexName)
        {
            bool retVal = false;
            try
            {
                string sql = "DROP INDEX " + indexName;
                XSafeCommand(connDesc, sql, null);
                retVal = true;
            }
            catch (Exception e)
            {
                _log.Warn("Unable to drop index '" + indexName + "'.", e);
            }
            return retVal;
        }

        /// <summary>
        /// Creates a non-unique index on a database table.
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="indexName">Name of the index to create.</param>
        /// <param name="tableName">What table to create the index on.</param>
        /// <param name="columnNames">The columns included in the index.</param>
        public static void CreateIndex(AbstractSqlConnectionDescriptor connDesc, string indexName, string tableName,
                                       IEnumerable<string> columnNames)
        {
            CreateIndex(connDesc, indexName, false, tableName, columnNames);
        }

        /// <summary>
        /// Creates an index on a database table.
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="indexName">Name of the index to create.</param>
        /// <param name="isUnique">Is this a unique index?</param>
        /// <param name="tableName">What table to create the index on.</param>
        /// <param name="columnNames">The columns included in the index.</param>
        public static void CreateIndex(AbstractSqlConnectionDescriptor connDesc, string indexName, bool isUnique, string tableName,
                                       IEnumerable<string> columnNames)
        {
            string sql = connDesc.MakeCreateIndexCommand(indexName, isUnique, tableName, columnNames);
            XSafeCommand(connDesc, sql, null);
        }

        /// <summary>
        /// Truncates a table if supported by the DB, otherwise deletes all rows (which is
        /// effectively the same, but potentially a lot slower).
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="tableName">What table we want to blow away the contents of.</param>
        public static void TruncateTable(AbstractSqlConnectionDescriptor connDesc, string tableName)
        {
            StringBuilder sql = DbCaches.StringBuilders.Get();
            if (connDesc.SupportsTruncate())
            {
                sql.Append("TRUNCATE TABLE ");
            }
            else
            {
                sql.Append("DELETE FROM ");
            }
            sql.Append(tableName);
            XSafeCommand(connDesc, sql.ToString(), null);
            DbCaches.StringBuilders.Return(sql);
        }

        /// <summary>
        /// Similar to DbConnection.GetSchema, except this keeps the connection
        /// handling logic here in the utility class.
        /// </summary>
        /// <param name="connDesc">Connection descriptor for the database.</param>
        /// <returns>A DataTable, same as DbConnection.GetSchema.</returns>
        public static DataTable GetSchema(AbstractSqlConnectionDescriptor connDesc)
        {
            DataTable retVal;
            IDbConnection conn = DbCaches.Connections.Get(connDesc);
            try
            {
                if (conn is DbConnection)
                {
                    retVal = ((DbConnection)conn).GetSchema();
                }
                else
                {
                    throw new BadConnectionTypeException(connDesc,
                            "The IDbConnection from this connection descriptor cannot return schema info.");
                }
            }
            finally
            {
                DbCaches.Connections.Return(connDesc, conn);
            }
            return retVal;
        }

        /// <summary>
        /// Similar to DbConnection.GetSchema, except this keeps the connection
        /// handling logic here in the utility class.
        /// </summary>
        /// <param name="connDesc">Connection descriptor for the database.</param>
        /// <param name="name">The name of the type of object you want schema info on.
        ///                    For example, "Columns" to get info on the columns.</param>
        /// <param name="restrictions">A magic string array that means something to
        ///                            DbConnection.GetSchema.  Should be a string array
        ///                            of the following restrictions:
        ///                            [0] = Catalog
        ///                            [1] = Owner
        ///                            [2] = Table
        ///                            [3] = TableType
        ///                            Any/all may be null.</param>
        /// <returns>A DataTable, same as DbConnection.GetSchema.</returns>
        public static DataTable GetSchema(AbstractSqlConnectionDescriptor connDesc, string name, string[] restrictions)
        {
            DataTable retVal;
            IDbConnection conn = DbCaches.Connections.Get(connDesc);
            try
            {
                if (conn is DbConnection)
                {
                    retVal = ((DbConnection) conn).GetSchema(name, restrictions);
                }
                else
                {
                    throw new BadConnectionTypeException(connDesc,
                            "The IDbConnection from this connection descriptor cannot return schema info.");
                }
            }
            finally
            {
                DbCaches.Connections.Return(connDesc, conn);
            }
            return retVal;
        }

        /// <summary>
        /// Generates a simplistic classmapping (without changing any column/field names) from a
        /// table's schema.  Intended for times when you are transferring data without really using it,
        /// for example from a DB to a CSV.  Will most likely only be useful for a DictionaryDAO since
        /// there is no defined .NET class to map to.
        /// 
        /// All column names will be returned in caps.
        /// </summary>
        /// <param name="connDesc">Connection descriptor for the database.</param>
        /// <param name="tableName">Name of the table to generate a mapping for.</param>
        /// <returns>A class mapping, the type name will be the table name, every column will be
        ///          mapped to a "field" of the same name, and no types will be specified.</returns>
        public static ClassMapping GenerateMappingFromSchema(AbstractSqlConnectionDescriptor connDesc,
            string tableName)
        {
            return GenerateMappingFromSchema(connDesc, tableName, null);
        }

        /// <summary>
        /// Generates a simplistic classmapping (without changing any column/field names) from a
        /// table's schema.  Intended for times when you are transferring data without really using it,
        /// for example from a DB to a CSV.  Will most likely only be useful for a DictionaryDAO since
        /// there is no defined .NET class to map to.
        /// 
        /// All column names will be returned in caps.
        /// </summary>
        /// <param name="connDesc">Connection descriptor for the database.</param>
        /// <param name="tableName">Name of the table to generate a mapping for.</param>
        /// <param name="columnSorter">Since auto-generated column mappings are likely used for
        ///                            purposes such as exporting to a CSV, if you wish the columns
        ///                            to be sorted in some sort of order, you may provide an
        ///                            IComparer to do so.  May be null.</param>
        /// <returns>A class mapping, the type name will be the table name, every column will be
        ///          mapped to a "field" of the same name, and no types will be specified.</returns>
        public static ClassMapping GenerateMappingFromSchema(AbstractSqlConnectionDescriptor connDesc,
            string tableName, IComparer<ClassMapColDefinition> columnSorter)
        {
            List<ClassMapColDefinition> mapCols = new List<ClassMapColDefinition>();
            // Tolerate fully qualified table names.
            string[] tableNameParts = tableName.Split('.');
            string[] searchRestrictions = new string[4];
            // Copy the parts back starting at index 2 (2 = unadorned table name, 1 = namespace, etc).
            int destIdx = 2;
            for (int x = tableNameParts.Length - 1; x >= 0 && destIdx >= 0; x--)
            {
                searchRestrictions[destIdx--] = tableNameParts[x];
            }
            DataTable schema = GetSchema(connDesc, "Columns", searchRestrictions);
            // Now this should have just the columns that are on the table we want.
            foreach (DataRow row in schema.Rows)
            {
                // The first three columns in the data rows are the fully qualified table name.
                // Since all we need are the columns, we can ignore them.
                if (!row.IsNull(3))
                {
                    string colName = row[3].ToString().ToUpper();
                    mapCols.Add(new ClassMapColDefinition(colName, colName, null));
                }
            }
            if (columnSorter != null)
            {
                mapCols.Sort(columnSorter);
            }
            return new ClassMapping(tableName, tableName, mapCols, false);
        }

        #endregion

        #region Internal/Private
        /// <summary>
        /// Similar to the "XSafeQuery" method, except this executes a
        /// query that returns a single result.
        /// </summary>
        /// <param name="connDesc">The database connection descriptor.  This is used both as
        ///                        a key for caching connections/commands as well as for
        ///                        getting the actual database connection the first time.</param>
        /// <param name="transaction">The transaction to do this as part of.</param>
        /// <param name="sql">The SQL query to execute.</param>
        /// <param name="sqlParams">A list of objects to use as parameters
        ///							to the SQL statement.  The list may be
        ///							null if there are no parameters.</param>
        /// <returns>The single result returned by the query.</returns>
        private static object XSafeScalarQuery(AbstractSqlConnectionDescriptor connDesc, 
            SqlTransaction transaction, string sql, IEnumerable sqlParams)
        {
            object retVal;
            IDbConnection conn = transaction != null
                ? transaction.Connection
                : DbCaches.Connections.Get(connDesc);
            try
            {
                IDbCommand cmd = DbCaches.Commands.Get(sql, conn);
                if (transaction != null)
                {
                    cmd.Transaction = transaction.Transaction;
                }
                try
                {
                    SetSQLOnCommand(connDesc, cmd, sql, sqlParams);
                    try
                    {
                        retVal = cmd.ExecuteScalar();
                    }
                    catch (Exception e)
                    {
                        throw new UnableToRunSqlException(connDesc, sql, sqlParams, e);
                    }
                }
                finally
                {
                    DbCaches.Commands.Return(sql, conn, cmd);
                }
            }
            finally
            {
                if (transaction == null)
                {
                    DbCaches.Connections.Return(connDesc, conn);
                }
            }
            return retVal;
        }

        /// <summary>
        /// Just reads the results, ToString's 'em, and puts them into the parameters
        /// hashtable as a list called 'results'.
        /// </summary>
        private static void ReadStringsFromQuery(Hashtable parameters, IDataReader reader)
        {
            IList<string> results = DbCaches.StringLists.Get();
            parameters["results"] = results;

            int count = 0;
            while (reader.Read())
            {
                if (++count % 1000 == 0)
                {
                    _log.Debug("Read strings count: " + count);
                }
                if (reader.IsDBNull(0))
                {
                    results.Add(null);
                }
                else
                {
                    results.Add(reader.GetValue(0).ToString());
                }
            }
        }

        /// <summary>
        /// Just reads the results, Convert.ToInt32's 'em, and puts them into the parameters
        /// hashtable as a list called 'results'.  Nulls are ignored!
        /// </summary>
        private static void ReadIntsFromQuery(Hashtable parameters, IDataReader reader)
        {
            IList<int> results = DbCaches.IntLists.Get();
            parameters["results"] = results;

            int count = 0;
            while (reader.Read())
            {
                if (++count % 1000 == 0)
                {
                    _log.Debug("Read ints count: " + count);
                }
                if (!reader.IsDBNull(0))
                {
                    results.Add(Convert.ToInt32(reader.GetValue(0)));
                }
            }
        }

        /// <summary>
        /// Helper to set up a command with sql and parameters and other misc stuff.
        /// </summary>
        private static void SetSQLOnCommand(AbstractSqlConnectionDescriptor connDesc, 
            IDbCommand cmd, string sql, IEnumerable sqlParams)
        {
            if (connDesc == null)
            {
                throw new ArgumentNullException("connDesc", "Connection descriptor cannot be null.");
            }
            if (cmd == null)
            {
                throw new ArgumentNullException("cmd", "Command cannot be null.");
            }
            try
            {
                cmd.CommandType = CommandType.Text;
                cmd.CommandText = sql;
                if (sqlParams != null)
                {
                    connDesc.SetParametersOnCommand(cmd, sqlParams);
                }
            }
            catch (Exception e)
            {
                _log.Warn("Unable to set parameters on sql command, " +
                          SqlUtilities.SqlParamsToString(sql, sqlParams), e);
                throw new ApplicationException("Bad parameters for sql statement.", e);
            }
        }

        #endregion
    }
}