﻿using org.rufwork.mooresDb.infrastructure;
using org.rufwork.mooresDb.infrastructure.contexts;
using org.rufwork.mooresDb.infrastructure.serializers;
using org.rufwork.mooresDb.infrastructure.tableParts;
using org.rufwork.utils;
using org.rufwork.extensions;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using org.rufwork.mooresDb.exceptions;

namespace org.rufwork.mooresDb.infrastructure.commands.Processors
{
    public class WhereProcessor
    {
        public delegate object RowProcessor(byte[] abytRow, Column[] acolsInSelect, Dictionary<string, string> dictColNameMapping, TableContext table, ref DataTable dt);

        private static bool _colsAreCompatible(Column colToUpdate, Column colSource)
        {
            bool bCompatible = false;

            if (colToUpdate.colType.Equals(COLUMN_TYPES.AUTOINCREMENT))
            {
                bCompatible = false;    // hard stop.  Can't update autoincrement fields.
            }
            else if (colToUpdate.colType.Equals(colSource.colType))
            {
                bCompatible = true;
            }
            else
            {
                // TODO: Are there other sets we want to consider equivalent?
                Queue<COLUMN_TYPES[]> qLinkedTypes = new Queue<COLUMN_TYPES[]>();
                COLUMN_TYPES[] sharedIntTypes = { COLUMN_TYPES.INT, COLUMN_TYPES.AUTOINCREMENT, COLUMN_TYPES.TINYINT };
                qLinkedTypes.Enqueue(sharedIntTypes);

                foreach (COLUMN_TYPES[] relatedColTypes in qLinkedTypes)
                {
                    if (sharedIntTypes.Contains(colToUpdate.colType) && sharedIntTypes.Contains(colSource.colType))
                    {
                        bCompatible = true;
                        break;
                    }
                }
            }
            return bCompatible && colToUpdate.intColLength >= colSource.intColLength;
        }

        public static void ProcessRows(ref DataTable dtWithCols,
            TableContext table,
            CommandParts commandParts
        )
        {
            string strWhere = commandParts.strWhere;

            List<Comparison> lstWhereConditions = _CreateWhereConditions(strWhere, table);

            using (BinaryReader b = new BinaryReader(File.Open(table.strTableFileLoc, FileMode.Open)))
            {
                int intRowCount = table.intFileLength / table.intRowLength;
                b.BaseStream.Seek(2 * table.intRowLength, SeekOrigin.Begin);  // TODO: Code more defensively in case it's somehow not the right/minimum length

                for (int i = 2; i < intRowCount; i++)
                {
                    byte[] abytRow = b.ReadBytes(table.intRowLength);
                    bool bMatchingRow = true;

                    // Check and make sure this is an active row, and has 
                    // the standard row lead byte, 0x11.  If not, the row
                    // should not be read.
                    // I'm going to switch this to make it more defensive 
                    // and a little easier to follow.
                    switch (abytRow[0])
                    {
                        case 0x88:
                            // DELETED
                            bMatchingRow = false;
                            break;

                        case 0x11:
                            // ACTIVE
                            // Find if the WHERE clause says to exclude this row.
                            foreach (Comparison comparison in lstWhereConditions)
                            {
                                // For now, we're (somewhat clumsily) processing INs as lots of small ORs.
                                // And no, we're not actually supporting the OR statement in a regular WHERE yet.
                                if (comparison is CompoundComparison)
                                {
                                    bool bInKeeper = false;
                                    // Could use a lot more indexed logic here, but that'll need to be
                                    // an extension to this package to keep the logic simple.
                                    // This is a painful, bullheaded Moore's comparison.
                                    foreach (Comparison compInner in ((CompoundComparison)comparison).lstComparisons)
                                    {
                                        if (_ComparisonEngine(compInner, abytRow))
                                        {
                                            bInKeeper = true;
                                            break;
                                        }
                                    }
                                    bMatchingRow = bMatchingRow && bInKeeper;
                                }
                                else
                                {
                                    bMatchingRow = bMatchingRow && _ComparisonEngine(comparison, abytRow);
                                }
                            }
                            break;

                        default:
                            throw new Exception("Unexpected row state in SELECT: " + abytRow[0]);
                    }

                    if (bMatchingRow)
                    {
                        switch (commandParts.commandType)
                        {
                            case CommandParts.COMMAND_TYPES.SELECT:
                                Dictionary<string, string> dictFuzzyToColName = new Dictionary<string,string>(commandParts.dictFuzzyToColNameMappings); // resets with each row.
                                DataRow row = dtWithCols.NewRow();
                                foreach (Column mCol in commandParts.acolInSelect)
                                {
                                    byte[] abytCol = new byte[mCol.intColLength];
                                    Array.Copy(abytRow, mCol.intColStart, abytCol, 0, mCol.intColLength);
                                    //Console.WriteLine(System.Text.Encoding.Default.GetString(abytCol));

                                    // now translate/cast the value to the column in the row.
                                    // OLD:  row[OperativeName(mCol.strColName, dictColNameMapping)] = Router.routeMe(mCol).toNative(abytCol);
                                    // foreach b/c we're supporting multiple calls to the same col in a SELECT now.
                                    foreach (DataColumn dc in dtWithCols.Columns)
                                    {
                                        // See if we should use this column's (mCol's) value with this DataColumn.
                                        if (dictFuzzyToColName.ContainsValue(mCol.strColName) || mCol.strColName.Equals(dc.ColumnName))
                                        {
                                            // If so, see if there's a fuzzy name mapped for this column.
                                            string strColName = GetFuzzyNameIfExists(mCol.strColName, dictFuzzyToColName);
                                            row[strColName] = Router.routeMe(mCol).toNative(abytCol);
                                            // If we had a fuzzy name, remove from the dictionary so we don't dupe it.
                                            if (dictFuzzyToColName.ContainsKey(strColName))
                                            {
                                                dictFuzzyToColName.Remove(strColName);
                                            }
                                        }
                                    }
                                }
                                dtWithCols.Rows.Add(row);
                                break;

                            case CommandParts.COMMAND_TYPES.UPDATE:
                                // kludge for fuzzy names:
                                // (This should be a one-way process, so I don't think having the logic
                                // in this cruddy a place is a huge problem that'll cause wasted 
                                // resources; it's just having me rethink fuzzy names in general.)
                                Dictionary<string, string> dictLaunderedUpdateVals = new Dictionary<string,string>();

                                foreach (string key in commandParts.dictUpdateColVals.Keys)
                                {
                                    dictLaunderedUpdateVals.Add(table.getRawColName(key), commandParts.dictUpdateColVals[key]);
                                }
                                
                                foreach (Column mCol in table.getColumns())
                                {
                                    if (dictLaunderedUpdateVals.ContainsKey(mCol.strColName))
                                    {
                                        // Column needs updating; take values from update
                                        byte[] abytVal = null; // Will hold "raw" value.  Might not be the full column length.

                                        // Check to see if we're updating using another column from the same row or a value.
                                        // TODO: Performance here should be crappy.  Create a mapping of col names & Cols for
                                        // in-statement column value transfers.  ie, "UPDATE table1 SET col1 = col2 WHERE col1 = 'update me';"
                                        string valueAsString = dictLaunderedUpdateVals[mCol.strColName];
                                        Column colToPullValueFrom = table.getColumnByName(valueAsString);

                                        if (null != colToPullValueFrom)
                                        {
                                            if (mCol.intColLength < colToPullValueFrom.intColLength || !_colsAreCompatible(mCol, colToPullValueFrom))
                                            {
                                                throw new Exception("UPDATE attempted to update with a value that was potentially too large or with columns of incompatible types.");
                                            }
                                            abytVal = new byte[colToPullValueFrom.intColLength];
                                            Array.Copy(abytRow, colToPullValueFrom.intColStart, abytVal, 0, colToPullValueFrom.intColLength);
                                        }
                                        else
                                        {
                                            BaseSerializer serializer = Router.routeMe(mCol);
                                            abytVal = serializer.toByteArray(dictLaunderedUpdateVals[mCol.strColName]);
                                        }

                                        // double check that the serializer at least
                                        // gave you a value that's the right length so
                                        // that everything doesn't go to heck (moved where 
                                        // that was previously checked into the serializers)
                                        if (abytVal.Length != mCol.intColLength)
                                        {
                                            throw new Exception("Improperly lengthed field from serializer (UPDATE): " + mCol.strColName);
                                        }

                                        // keep in mind that column.intColLength should always match abytColValue.Length.  While I'm
                                        // testing, I'm going to put in this check, but at some point, you should be confident enough
                                        // to consider removing this check.
                                        if (abytVal.Length != mCol.intColLength)
                                        {
                                            throw new Exception("Surprising value and column length mismatch");
                                        }

                                        Buffer.BlockCopy(abytVal, 0, abytRow, mCol.intColStart, abytVal.Length);
                                    }   // else don't touch what's in the row; it's not an updated colum
                                }
  
                                b.BaseStream.Seek(-1 * table.intRowLength, SeekOrigin.Current);
                                b.BaseStream.Write(abytRow, 0, abytRow.Length);

                                break;

                            case CommandParts.COMMAND_TYPES.DELETE:
                                byte[] abytErase = new byte[table.intRowLength];   // should be initialized to zeroes.
                                // at least to test, I'm going to write it all over with 0x88s.
                                for (int j = 0; j < table.intRowLength; j++) { abytErase[j] = 0x88; }

                                // move pointer back to the first byte of this row.
                                b.BaseStream.Seek(-1 * table.intRowLength, SeekOrigin.Current);
                                b.BaseStream.Write(abytErase, 0, abytErase.Length);
                                break;

                            default:
                                throw new Exception("Unhandled command type in WhereProcessor: " + commandParts.commandType);
                        }
                    }
                }
            }
            // nothing to return -- dt was passed by ref.
        }

        // This subs in the name used in the SELECT if it's a fuzzy matched column.
        // TODO: Seems like this might belong on the TableContext?
        // TODO: Looking it up with every row is pretty danged inefficient.
        // Flipping the key/value here b/c the fuzzy name is unique, not the strict name,
        // now that we're supporting the same column multiple times with different [fuzzy, for now]
        // names.
        // TODO: Allow column name aliases.
        public static string GetFuzzyNameIfExists(string strStrictColName, Dictionary<string, string> dictNameMapping)
        {
            string strReturn = strStrictColName;
            if (dictNameMapping.ContainsValue(strStrictColName))
            {
                strReturn = dictNameMapping.XGetFirstKeyByValue<string>(strStrictColName);
            }
            return strReturn;
        }

        /// <summary>
        /// Takes in a row's worth of bytes in a byte array and sees
        /// if the row's proper value matches the active comparison.
        /// </summary>
        /// <returns></returns>
        private static bool _ComparisonEngine(Comparison comparison, byte[] abytRow)
        {
            byte[] abytRowValue = new byte[comparison.colRelated.intColLength];
            Array.Copy(abytRow, comparison.colRelated.intColStart, abytRowValue, 0, comparison.colRelated.intColLength);

            // TODO: This is ugly.  Having CompareByteArrays on the serializers is ungainly at best.  Just not sure where else to put it.
            COMPARISON_TYPE? valueRelationship = Router.routeMe(comparison.colRelated).CompareBytesToVal(abytRowValue, comparison.objParsedValue);

            if (null == valueRelationship)
            {
                throw new Exception("Invalid value comparison in SELECT");
            }

            return valueRelationship == comparison.comparisonType;
        }

#region whereToComparisons
        private static List<Comparison> _CreateWhereConditions(string strWhere, TableContext table)
        {
            List<Comparison> lstReturn = new List<Comparison>();

            if (!string.IsNullOrWhiteSpace(strWhere))
            {
                strWhere = strWhere.Substring(6);
                string[] astrClauses = Utils.SplitSeeingQuotes(strWhere, "AND", false).ToArray();

                // TODO: Handle NOTs, duh.
                for (int i = 0; i < astrClauses.Length; i++)
                {
                    Comparison comparison = null;
                    string strClause = astrClauses[i].Trim();
                    if (MainClass.bDebug) Console.WriteLine("Where clause #" + i + " " + strClause);

                    if (Utils.SplitSeeingQuotes(strClause, " IN ", false).Count > 1)
                    {
                        CompoundComparison inClause = new CompoundComparison(GROUP_TYPE.OR);
                        if (MainClass.bDebug) Console.WriteLine("IN clause: " + strClause);
                        string strField = strClause.Substring(0, strClause.IndexOf(' '));

                        string strIn = strClause.Substring(strClause.IndexOf('(') + 1, strClause.LastIndexOf(')') - strClause.IndexOf('(') - 1);
                        string[] astrInVals = strIn.Split(',');
                        foreach (string strInVal in astrInVals)
                        {
                            string strFakeWhere = strField + " = " + strInVal;
                            inClause.lstComparisons.Add(WhereProcessor._CreateComparison(strFakeWhere, table));
                        }
                        lstReturn.Add(inClause);
                    }
                    else
                    {
                        comparison = WhereProcessor._CreateComparison(strClause, table);
                        if (null != comparison)
                        {
                            lstReturn.Add(comparison);
                        }
                        else
                        {
                            Console.WriteLine("Uncaptured WHERE clause type: " + strClause);
                        }
                    }
                }
            }

            return lstReturn;
        }

        private static Comparison _CreateComparison(string strClause, TableContext table)
        {
            char[] achrOperator = {'='};
            if (strClause.Contains('<'))
            {
                achrOperator[0] = '<';
            }
            else if (strClause.Contains('>'))
            {
                achrOperator[0] = '>';
            }
            else if (!strClause.Contains('='))
            {
                throw new Exception("Illegal comparison type in SelectCommand: " + strClause);
            }

            string[] astrComparisonParts = strClause.Split(achrOperator, 2);
            Column colToConstrain = table.getColumnByName(astrComparisonParts[0].Trim());
            if (null == colToConstrain)
            {
                throw new ColumnNotFoundException("Column not found in SELECT statement: " + astrComparisonParts[0]);
            }

            BaseSerializer serializer = Router.routeMe(colToConstrain);
            byte[] abytComparisonVal = serializer.toByteArray(astrComparisonParts[1].Trim());

            return new Comparison(achrOperator[0], colToConstrain, abytComparisonVal);
        }
#endregion whereToComparisons
    }
}
