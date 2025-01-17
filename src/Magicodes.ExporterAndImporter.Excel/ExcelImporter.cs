﻿using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Core.Models;
using Magicodes.ExporterAndImporter.Excel.Utility;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Magicodes.ExporterAndImporter.Excel
{
    /// <summary>
    ///  通用Excel导入类
    /// </summary>
    public class ExcelImporter : IImporter
    {
        /// <summary>
        /// 单个sheet页限制最大导入行数
        /// </summary>
        private const int _maxRowNumber = 65000;

        /// <summary>
        ///     生成Excel导入模板
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="fileName"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException">文件名必须填写! - fileName</exception>
        public Task<ExcelFileInfo> GenerateTemplate<T>(string fileName) where T : class
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ImportException("文件名必须填写!");
            }

            ExcelFileInfo fileInfo = ExcelHelper.CreateExcelPackage(fileName, excelPackage =>
             {
                 StructureExcel<T>(excelPackage);
             });

            return Task.FromResult(fileInfo);
        }

        /// <summary>
        ///     生成Excel导入模板
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns>二进制字节</returns>
        public Task<byte[]> GenerateTemplateByte<T>() where T : class
        {
            using (var excelPackage = new ExcelPackage())
            {
                StructureExcel<T>(excelPackage);

                return Task.FromResult(excelPackage.GetAsByteArray());
            }
        }

        /// <summary>
        /// 导入模型验证数据
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="filePath"></param>
        /// <returns></returns>
        public Task<ImportModel<T>> Import<T>(string filePath) where T : class, new()
        {
            CheckImportFile(filePath);

            using (Stream stream = new FileStream(filePath, FileMode.Open))
            {
                return Import<T>(stream);
            }
        }

        /// <summary>
        /// 导入模型验证数据
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="stream">文件流</param>
        /// <returns></returns>
        public Task<ImportModel<T>> Import<T>(Stream stream) where T : class, new()
        {
            IList<ValidationResultModel> validationResultModels = new List<ValidationResultModel>();
            using (var excelPackage = new ExcelPackage(stream))
            {
                bool hasValidTemplate = ParseTemplate<T>(excelPackage, out var columnHeaders);
                IList<T> importDataModels = new List<T>();
                if (!hasValidTemplate)
                {
                    return Task.FromResult(new ImportModel<T>
                    {
                        HasValidTemplate = hasValidTemplate,
                        Data = importDataModels,
                        ValidationResults = validationResultModels
                    });
                }

                importDataModels = ParseData<T>(excelPackage, columnHeaders);
                string keyName = "Invalid";
                for (var i = 0; i < importDataModels.Count; i++)
                {
                    var validationResultModel = new ValidationResultModel
                    {
                        Index = i + 1
                    };

                    var isValid = ValidatorHelper.TryValidate(importDataModels[i], out var validationResults);
                    if (isValid)
                    {
                        continue;
                    }

                    if (!validationResultModel.Errors.ContainsKey(keyName))
                    {
                        validationResultModel.Errors.Add(keyName, "导入数据无效");
                    }

                    foreach (var validationResult in validationResults)
                    {
                        var key = validationResult.MemberNames.First();
                        var column = columnHeaders.FirstOrDefault(a => a.PropertyName.Equals(key));
                        if (column != null)
                        {
                            key = column.ExporterHeader.Name;
                        }

                        var value = validationResult.ErrorMessage;
                        if (validationResultModel.FieldErrors.ContainsKey(key))
                        {
                            validationResultModel.FieldErrors[key] = validationResultModel.FieldErrors[key] + "," + value;
                        }
                        else
                        {
                            validationResultModel.FieldErrors.Add(key, value);
                        }
                    }

                    if (validationResultModel.Errors.Count > 0)
                    {
                        validationResultModels.Add(validationResultModel);
                    }
                }

                return Task.FromResult(new ImportModel<T>
                {
                    HasValidTemplate = hasValidTemplate,
                    Data = importDataModels,
                    ValidationResults = validationResultModels
                });
            }
        }

        /// <summary>
        /// 检查导入文件路劲
        /// </summary>
        /// <exception cref="ArgumentException">文件路径不能为空! - filePath</exception>
        private static void CheckImportFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ImportException("文件路径不能为空!");
            }

            //TODO:在Docker容器中存在文件路径找不到问题，暂时先注释掉
            //if (!File.Exists(filePath))
            //{
            //    throw new ImportException("导入文件不存在!");
            //}
        }

        /// <summary>
        /// 解析头部
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        /// <exception cref="ArgumentException">导入实体没有定义ImporterHeader属性</exception>
        private static bool ParseImporterHeader<T>(out List<ImporterHeaderInfo> importerHeaderList,
            out Dictionary<int, IDictionary<string, int>> enumColumns, out List<int> boolColumns)
        {
            importerHeaderList = new List<ImporterHeaderInfo>();
            enumColumns = new Dictionary<int, IDictionary<string, int>>();
            boolColumns = new List<int>();

            var objProperties = typeof(T).GetProperties();
            if (objProperties == null || objProperties.Length <= 0)
            {
                return false;
            }

            for (var i = 0; i < objProperties.Length; i++)
            {
                var importerHeaderAttribute =
                    (objProperties[i].GetCustomAttributes(typeof(ImporterHeaderAttribute), true) as
                        ImporterHeaderAttribute[])?.FirstOrDefault();
                if (importerHeaderAttribute == null || string.IsNullOrWhiteSpace(importerHeaderAttribute.Name))
                {
                    throw new ImportException($"导入实体{typeof(T)}的字段{objProperties[i].ToString()}未定义ImporterHeaderAttribute属性");
                }

                var requiredAttribute = (objProperties[i].GetCustomAttributes(typeof(RequiredAttribute), true) as
                    RequiredAttribute[])?.FirstOrDefault();
                importerHeaderList.Add(new ImporterHeaderInfo
                {
                    IsRequired = requiredAttribute != null,
                    PropertyName = objProperties[i].Name,
                    ExporterHeader = importerHeaderAttribute
                });

                if ("enum".Equals(objProperties[i].PropertyType.BaseType?.Name.ToLower()))
                {
                    enumColumns.Add(i + 1, EnumHelper.GetDisplayNames(objProperties[i].PropertyType));
                }

                if (objProperties[i].PropertyType == typeof(bool))
                {
                    boolColumns.Add(i + 1);
                }
            }

            return true;
        }

        /// <summary>
        ///     构建Excel
        /// </summary>
        /// <typeparam name="T"></typeparam>
        private static void StructureExcel<T>(ExcelPackage excelPackage) where T : class
        {
            var worksheet = excelPackage.Workbook.Worksheets.Add(typeof(T).Name);
            if (!ParseImporterHeader<T>(out var columnHeaders, out var enumColumns, out var boolColumns)) return;

            for (var i = 0; i < columnHeaders.Count; i++)
            {
                worksheet.Cells[1, i + 1].Value = columnHeaders[i].ExporterHeader.Name;
                if (!string.IsNullOrWhiteSpace(columnHeaders[i].ExporterHeader.Description))
                {
                    worksheet.Cells[1, i + 1].AddComment(columnHeaders[i].ExporterHeader.Description, columnHeaders[i].ExporterHeader.Author);
                }

                if (columnHeaders[i].IsRequired)
                {
                    worksheet.Cells[1, i + 1].Style.Font.Color.SetColor(Color.Red);
                }
            }
            worksheet.Cells.AutoFitColumns();
            worksheet.Cells.Style.WrapText = true;
            worksheet.Cells[worksheet.Dimension.Address].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
            worksheet.Cells[worksheet.Dimension.Address].Style.VerticalAlignment = ExcelVerticalAlignment.Center;

            worksheet.Cells[worksheet.Dimension.Address].Style.Border.Left.Style = ExcelBorderStyle.Thin;
            worksheet.Cells[worksheet.Dimension.Address].Style.Border.Right.Style = ExcelBorderStyle.Thin;
            worksheet.Cells[worksheet.Dimension.Address].Style.Border.Top.Style = ExcelBorderStyle.Thin;
            worksheet.Cells[worksheet.Dimension.Address].Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
            worksheet.Cells[worksheet.Dimension.Address].Style.Fill.PatternType = ExcelFillStyle.Solid;
            worksheet.Cells[worksheet.Dimension.Address].Style.Fill.BackgroundColor.SetColor(Color.DarkSeaGreen);

            foreach (var enumColumn in enumColumns)
            {
                var range = ExcelCellBase.GetAddress(1, enumColumn.Key, ExcelPackage.MaxRows, enumColumn.Key);
                var dataValidations = worksheet.DataValidations.AddListValidation(range);
                foreach (var displayName in enumColumn.Value) dataValidations.Formula.Values.Add(displayName.Key);
            }

            foreach (var boolColumn in boolColumns)
            {
                var range = ExcelCellBase.GetAddress(1, boolColumn, ExcelPackage.MaxRows, boolColumn);
                var dataValidations = worksheet.DataValidations.AddListValidation(range);
                dataValidations.Formula.Values.Add("是");
                dataValidations.Formula.Values.Add("否");
            }
        }

        /// <summary>
        ///     解析模板
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        private bool ParseTemplate<T>(ExcelPackage excelPackage, out List<ImporterHeaderInfo> columnHeaders)
            where T : class
        {
            ExcelImporterAttribute excelImporterAttribute = GetImporterAttribute<T>();
            if (null == excelImporterAttribute || string.IsNullOrWhiteSpace(excelImporterAttribute.SheetName))
            {
                throw new ImportException($"导入实体{typeof(T)}未定义ExcelImporterAttribute属性");
            }

            ExcelWorksheet worksheet = excelPackage.Workbook.Worksheets[excelImporterAttribute.SheetName];
            if (null == worksheet)
            {
                throw new ImportException($"读取名称为{excelImporterAttribute.SheetName}的sheet页异常，检查sheet页是否存在");
            }

            ParseImporterHeader<T>(out columnHeaders, out var enumColumns, out var boolColumns);
            for (var i = 0; i < columnHeaders.Count; i++)
            {
                var header = worksheet.Cells[1, i + 1].Text;
                if (columnHeaders[i].ExporterHeader != null &&
                    !string.IsNullOrWhiteSpace(columnHeaders[i].ExporterHeader.Name))
                {
                    if (!header.Equals(columnHeaders[i].ExporterHeader.Name))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!header.Equals(columnHeaders[i].PropertyName))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        ///     解析数据
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        /// <exception cref="ArgumentException">最大允许导入条数不能超过5000条</exception>
        private IList<T> ParseData<T>(ExcelPackage excelPackage, List<ImporterHeaderInfo> columnHeaders)
            where T : class, new()
        {
            ExcelImporterAttribute excelImporterAttribute = GetImporterAttribute<T>();
            if (null == excelImporterAttribute || string.IsNullOrWhiteSpace(excelImporterAttribute.SheetName))
            {
                throw new ImportException($"导入实体{typeof(T)}未定义ExcelImporterAttribute属性");
            }

            ExcelWorksheet worksheet = excelPackage.Workbook.Worksheets[excelImporterAttribute.SheetName];
            if (null == worksheet)
            {
                throw new ImportException($"读取名称为{excelImporterAttribute.SheetName}的sheet页异常，检查sheet页是否存在");
            }

            if (excelImporterAttribute.MaxRowNumber > _maxRowNumber)
            {
                excelImporterAttribute.MaxRowNumber = _maxRowNumber;
            }

            if (worksheet.Dimension.End.Row > excelImporterAttribute.MaxRowNumber)
            {
                throw new ImportException($"最大允许导入行数不能超过{excelImporterAttribute.MaxRowNumber}行");
            }

            IList<T> importDataModels = new List<T>();
            var propertyInfos = new List<PropertyInfo>(typeof(T).GetProperties());

            for (var index = 2; index <= worksheet.Dimension.End.Row; index++)
            {
                bool allRowIsEmpty = AllRowIsEmpty(worksheet, index);
                if (allRowIsEmpty)
                {
                    // 整行为空，则跳过
                    continue;
                }

                var dataItem = new T();
                foreach (var propertyInfo in propertyInfos)
                {
                    var cell = worksheet.Cells[index,
                        columnHeaders.FindIndex(a => a.PropertyName.Equals(propertyInfo.Name)) + 1];
                    switch (propertyInfo.PropertyType.BaseType?.Name.ToLower())
                    {
                        case "enum":
                            var enumDisplayNames = EnumHelper.GetDisplayNames(propertyInfo.PropertyType);
                            if (enumDisplayNames.ContainsKey(cell.Value?.ToString() ?? throw new ArgumentException()))
                            {
                                propertyInfo.SetValue(dataItem,
                                    enumDisplayNames[cell.Value?.ToString()]);
                            }
                            else
                            {
                                throw new ImportException($"值 {cell.Value} 不存在模板下拉选项中");
                            }

                            continue;
                    }

                    SetCellValue(propertyInfo, cell, dataItem);
                }

                importDataModels.Add(dataItem);
            }

            return importDataModels;
        }

        /// <summary>
        /// 获取导入全局定义
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        private static ExcelImporterAttribute GetImporterAttribute<T>() where T : class
        {
            var importerTableAttributes = (typeof(T).GetCustomAttributes(typeof(ExcelImporterAttribute), true) as ExcelImporterAttribute[]);
            if (importerTableAttributes != null && importerTableAttributes.Length > 0)
                return importerTableAttributes[0];

            var importerAttributes = (typeof(T).GetCustomAttributes(typeof(ImporterAttribute), true) as ImporterAttribute[]);

            if (importerAttributes != null && importerAttributes.Length > 0)
            {
                var export = importerAttributes[0];
                return new ExcelImporterAttribute()
                {
                    SheetName = typeof(T).Name
                };
            }

            return null;
        }

        /// <summary>
        /// 判断Excel是否整行都为空
        /// </summary>
        /// <param name="worksheet"></param>
        /// <param name="rowIndex"></param>
        /// <returns></returns>
        private static bool AllRowIsEmpty(ExcelWorksheet worksheet, int rowIndex)
        {
            bool allRowIsEmpty = true;
            int columnNum = worksheet.Dimension.End.Column;
            for (int columnIndex = 1; columnIndex <= columnNum; columnIndex++)
            {
                if (!"".Equals(worksheet.Cells[rowIndex, columnIndex].Text))
                {
                    allRowIsEmpty = false;
                    break;
                }
            }

            return allRowIsEmpty;
        }

        /// <summary>
        /// 填充属性值
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="propertyInfo"></param>
        /// <param name="cell"></param>
        /// <param name="dataItem"></param>
        private static void SetCellValue<T>(PropertyInfo propertyInfo, ExcelRange cell, T dataItem) where T : class
        {
            switch (propertyInfo.PropertyType.Name.ToLower())
            {
                case "boolean":
                    var value = false;
                    if (cell.Value != null)
                    {
                        value = "是".Equals(cell.Value.ToString());
                    }
                    propertyInfo.SetValue(dataItem, value);
                    break;

                case "string":
                    propertyInfo.SetValue(dataItem, cell.Value?.ToString());
                    break;

                case "long":
                case "int64":
                    long.TryParse(cell.Value?.ToString(), out long longValue);
                    propertyInfo.SetValue(dataItem, longValue);
                    break;

                case "int":
                case "int32":
                    int.TryParse(cell.Value?.ToString(), out int intValue);
                    propertyInfo.SetValue(dataItem, intValue);
                    break;

                case "decimal":
                    decimal.TryParse(cell.Value?.ToString(), out decimal decimalValue);
                    propertyInfo.SetValue(dataItem, decimalValue);
                    break;

                case "double":
                    double.TryParse(cell.Value?.ToString(), out double doubleValue);
                    propertyInfo.SetValue(dataItem, doubleValue);
                    break;

                case "datetime":
                    DateTime.TryParse(cell.Value?.ToString(), out DateTime dateTimeValue);
                    propertyInfo.SetValue(dataItem, dateTimeValue);
                    break;

                default:
                    propertyInfo.SetValue(dataItem, cell.Value?.ToString());
                    break;
            }
        }
    }
}