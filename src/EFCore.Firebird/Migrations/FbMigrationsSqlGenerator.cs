// Copyright (c) 2017 Jean Ressouche @SouchProd. All rights reserved.
// https://github.com/souchprod/SouchProd.EntityFrameworkCore.Firebird
// This code inherit from the .Net Foundation Entity Core repository (Apache licence)
// and from the Pomelo Foundation Mysql provider repository (MIT licence).
// Licensed under the MIT. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FirebirdSql.Data.FirebirdClient;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Internal;
using Microsoft.EntityFrameworkCore.Utilities;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore.Migrations
{
    public class FbMigrationsSqlGenerator : MigrationsSqlGenerator
    {
	    private static readonly Regex TypeRe = new Regex(@"([a-z0-9]+)\s*?(?:\(\s*(\d+)?\s*\))?", RegexOptions.IgnoreCase);
	    private readonly IFirebirdOptions _options;

	    public FbMigrationsSqlGenerator(
            [NotNull] MigrationsSqlGeneratorDependencies dependencies,
            [NotNull] IFirebirdOptions options)
            : base(dependencies)
        {
            _options = options;
        }

        protected override void Generate([NotNull] MigrationOperation operation, [CanBeNull] IModel model, [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            var createDatabaseOperation = operation as FbCreateDatabaseOperation;
            if (createDatabaseOperation != null)
            {
                Generate(createDatabaseOperation, model, builder);
                builder.EndCommand();
                return;
            }

            var dropDatabaseOperation = operation as FbDropDatabaseOperation;
            if (dropDatabaseOperation is FbDropDatabaseOperation)
            {
                Generate(dropDatabaseOperation, model, builder);
                builder.EndCommand();
                return;
            }

            base.Generate(operation, model, builder);
        }

        protected override void Generate(DropColumnOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            var identifier = Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema);
            var alterBase = $"ALTER TABLE {identifier} DROP {Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name)}";
            builder.Append(alterBase).Append(Dependencies.SqlGenerationHelper.StatementTerminator);
            EndStatement(builder);
        }

        protected override void Generate(AlterColumnOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            var type = operation.ColumnType;
            if (operation.ColumnType == null)
            {
                var property = FindProperty(model, operation.Schema, operation.Table, operation.Name);
                type = property != null
                    ? Dependencies.TypeMapper.GetMapping(property).StoreType
                    : Dependencies.TypeMapper.GetMapping(operation.ClrType).StoreType;
            }

            var identifier = Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema);
            var alterBase = $"ALTER TABLE {identifier} ALTER COLUMN {Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name)}";

            // TYPE
            builder.Append(alterBase)
                .Append(" ")
                .Append(type)
                .Append(operation.IsNullable ? " NULL" : " NOT NULL")
                .AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);

            if(!type.StartsWith("BLOB", StringComparison.Ordinal))
            {
                alterBase = $"ALTER TABLE {identifier} ALTER COLUMN {Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name)}";

                builder.Append(alterBase);

                if (operation.DefaultValue != null)
                {
                    var typeMapping = Dependencies.TypeMapper.GetMapping(operation.DefaultValue.GetType());
                    builder.Append(" SET DEFAULT ")
                        .Append(typeMapping.GenerateSqlLiteral(operation.DefaultValue))
                        .AppendLine(Dependencies.SqlGenerationHelper.BatchTerminator);
                }
                else if (!string.IsNullOrWhiteSpace(operation.DefaultValueSql))
                {
                    builder.Append(" SET DEFAULT ")
                        .Append(operation.DefaultValueSql)
                        .AppendLine(Dependencies.SqlGenerationHelper.BatchTerminator);
                }
                else
                {
                    builder.Append(" DROP DEFAULT;");
                }
            }

            EndStatement(builder);
        }

        protected override void Generate(CreateSequenceOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            throw new NotSupportedException("Firebird doesn't support sequence operation.");
        }

        protected override void Generate(RenameIndexOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            if (operation.NewName != null)
            {
                var createTableSyntax = _options.GetCreateTable(Dependencies.SqlGenerationHelper, operation.Table, operation.Schema);

                if (createTableSyntax == null)
                    throw new InvalidOperationException($"Could not find the CREATE TABLE DDL for the table: '{Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema)}'");

                var indexDefinitionRe = new Regex($"^\\s*((?:UNIQUE\\s)?KEY\\s)?{operation.Name}?(.*)$", RegexOptions.Multiline);
                var match = indexDefinitionRe.Match(createTableSyntax);

                string newIndexDefinition;
                if (match.Success)
                    newIndexDefinition = match.Groups[1].Value + Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName) + " " + match.Groups[2].Value.Trim().TrimEnd(',');
                else
                    throw new InvalidOperationException($"Could not find column definition for table: '{Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema)}' column: {operation.Name}");

                builder
                    .Append("ALTER TABLE ")
                    .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
                    .Append(" DROP INDEX ")
                    .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
                    .AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
                EndStatement(builder);
                                
                builder
                    .Append("ALTER TABLE ")
                    .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
                    .Append(" ADD ")
                    .Append(newIndexDefinition)
                    .AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);

                EndStatement(builder);
            }
        }

        protected override void Generate(RenameSequenceOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            throw new NotSupportedException("Firebird doesn't support sequence operation.");
        }

        protected override void Generate(RenameTableOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));
            
            // In Progress: Create a new temp table, move the data into, delete previous table.

            var createTableSyntax = _options.GetCreateTable(Dependencies.SqlGenerationHelper, operation.Name, operation.Schema);

            createTableSyntax = createTableSyntax?.Replace(operation.Name, operation.NewName) ?? throw new InvalidOperationException($"Could not find the CREATE TABLE DDL for the table: '{Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema)}'");

            builder.Append(createTableSyntax);

            builder
                .Append("INSERT INTO")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName, operation.Schema))
                .Append(" SELECT * FROM ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.NewSchema));

            EndStatement(builder);
        }

        protected override void Generate([NotNull] CreateIndexOperation operation, [CanBeNull] IModel model, [NotNull] MigrationCommandListBuilder builder, bool terminate)
        {
            var method = (string)operation[FbAnnotationNames.Prefix];

            builder.Append("CREATE ");

            if (operation.IsUnique)
            {
                builder.Append("UNIQUE ");
            }

            builder
                .Append("INDEX ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name.LimitLength(64)))
                .Append(" ON ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema));

            if (method != null)
            {
                builder
                    .Append(" USING ")
                    .Append(method);
            }

            builder
                .Append(" (")
                .Append(ColumnList(operation.Columns))
                .Append(")");

            if (terminate)
            {
                builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
                EndStatement(builder);
            }
        }

        protected override void Generate(
            [NotNull] CreateIndexOperation operation,
            [CanBeNull] IModel model,
            [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            Generate(operation, model, builder, true);
        }

        protected override void Generate(EnsureSchemaOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            throw new NotSupportedException("Firebird doesn't support EnsureSchema operation.");
        }

        public virtual void Generate(FbCreateDatabaseOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            throw new NotSupportedException("Firebird doesn't support CreateDatabase operation.");
        }

        public virtual void Generate(FbDropDatabaseOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            throw new NotSupportedException("Firebird doesn't support DropDatabase operation.");
        }

        protected override void Generate(DropIndexOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            builder
                .Append("ALTER TABLE ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
                .Append(" DROP CONSTRAINT ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
                .AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);

            EndStatement(builder);
        }

        protected override void Generate(
            [NotNull] RenameColumnOperation operation,
            [CanBeNull] IModel model,
            [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            builder.Append("ALTER TABLE ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
                .Append(" ALTER ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
                .Append(" TO ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName));
                
            EndStatement(builder);
        }

        protected override void ColumnDefinition(
            [CanBeNull] string schema, 
            [NotNull] string table, 
            [NotNull] string name, 
            [NotNull] Type clrType, 
            [CanBeNull] string type, 
            [CanBeNull] bool? unicode, 
            [CanBeNull] int? maxLength, 
            bool rowVersion, 
            bool nullable, 
            [CanBeNull] object defaultValue, 
            [CanBeNull] string defaultValueSql, 
            [CanBeNull] string computedColumnSql, 
            [NotNull] IAnnotatable annotatable, 
            [CanBeNull] IModel model, 
            [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotEmpty(name, nameof(name));
            Check.NotNull(annotatable, nameof(annotatable));
            Check.NotNull(clrType, nameof(clrType));
            Check.NotNull(builder, nameof(builder));

	        var matchType = type;
	        var matchLen = "";
	        var match = TypeRe.Match(type ?? "-");
	        if (match.Success)
	        {
		        matchType = match.Groups[1].Value.ToLower();
		        if (!string.IsNullOrWhiteSpace(match.Groups[2].Value))
			        matchLen = match.Groups[2].Value;
	        }

            var autoIncrement = false;
            var valueGenerationStrategy = annotatable[FbAnnotationNames.ValueGenerationStrategy] as FirebirdValueGenerationStrategy?;
           if ((valueGenerationStrategy == FirebirdValueGenerationStrategy.IdentityColumn) && string.IsNullOrWhiteSpace(defaultValueSql) && defaultValue == null)
            {
                switch (matchType)
                {
                    case "BIGINT":
                    case "INTEGER":
                        autoIncrement = true;
                        break;
                    case "TIMESTAMP":
                        defaultValueSql = $"CURRENT_TIMESTAMP";
                        break;
                }
            }
            
            string onUpdateSql = null;
            if (valueGenerationStrategy == FirebirdValueGenerationStrategy.ComputedColumn)
            {
	           switch (matchType)
	           {
	               case "TIMESTAMP":
                       if (string.IsNullOrWhiteSpace(defaultValueSql) && defaultValue == null)
                           defaultValueSql = $"CURRENT_TIMESTAMP";
			           onUpdateSql = $"CURRENT_TIMESTAMP";
			           break;
               }
            }

            builder
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(name))
                .Append(" ")
                .Append(type ?? GetColumnType(schema, table, name, clrType, unicode, maxLength, rowVersion, model));

            if (!nullable)
            {
                builder.Append((autoIncrement && _options.ConnectionSettings.ServerVersion.IdentityColumnsSupported
                    ? " GENERATED BY DEFAULT AS IDENTITY": "") + " NOT NULL");
            }

            if (!autoIncrement)
            {
                if (defaultValueSql != null)
                {
                    builder
                        .Append(" DEFAULT ")
                        .Append(defaultValueSql);
                }
                else if (defaultValue != null)
                {
                    var defaultValueLiteral = Dependencies.TypeMapper.GetMapping(clrType);
                    builder
                        .Append(" DEFAULT ")
                        .Append(defaultValueLiteral.GenerateSqlLiteral(defaultValue));
                }
                if (onUpdateSql != null)
                {
                    builder
                        .Append(" ON UPDATE ")
                        .Append(onUpdateSql);
                }
            }

        }

        protected override void DefaultValue(object defaultValue, string defaultValueSql, MigrationCommandListBuilder builder)
        {
            Check.NotNull(builder, nameof(builder));

            if (defaultValueSql != null)
            {
                builder
                    .Append(" DEFAULT ")
                    .Append(defaultValueSql);
            }
            else if (defaultValue != null)
            {
                var typeMapping = Dependencies.TypeMapper.GetMapping(defaultValue.GetType());
                builder
                    .Append(" DEFAULT ")
                    .Append(typeMapping.GenerateSqlLiteral(defaultValue));
            }
        }

        protected override void Generate([NotNull] DropForeignKeyOperation operation, [CanBeNull] IModel model, [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            builder
                .Append("ALTER TABLE ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
                .Append(" DROP CONSTRAINT ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
                .AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);

            EndStatement(builder);
        }

        protected override void Generate([NotNull] AddPrimaryKeyOperation operation, [CanBeNull] IModel model, [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            builder
                .Append("ALTER TABLE ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
                .Append(" ADD ");
           
            PrimaryKeyConstraint(operation, model, builder);

            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);

            var annotations = model.GetAnnotations();
            if (operation.Columns.Count() == 1)
            {
                // TODO
            }

            EndStatement(builder);
        }

        protected override void Generate([NotNull] DropPrimaryKeyOperation operation, [CanBeNull] IModel model,
            [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));
            builder
                .Append("ALTER TABLE ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
                .Append(" DROP CONSTRAINT ")
                .Append(operation.Name);

            EndStatement(builder);
        }

        public virtual void Rename(
            [CanBeNull] string schema,
            [NotNull] string name,
            [NotNull] string newName,
            [NotNull] string type,
            [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotEmpty(name, nameof(name));
            Check.NotEmpty(newName, nameof(newName));
            Check.NotEmpty(type, nameof(type));
            Check.NotNull(builder, nameof(builder));


            builder
                .Append("ALTER ")
                .Append(type)
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(name, schema))
                .Append(" TO ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(newName, schema));
        }
        
        protected override void ForeignKeyAction(ReferentialAction referentialAction, MigrationCommandListBuilder builder)
        {
            Check.NotNull(builder, nameof(builder));

            if (referentialAction == ReferentialAction.Restrict)
            {
                builder.Append("NO ACTION");
            }
            else
            {
                base.ForeignKeyAction(referentialAction, builder);
            }
        }

        protected override void ForeignKeyConstraint(
            [NotNull] AddForeignKeyOperation operation,
            [CanBeNull] IModel model,
            [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            if (operation.Name != null)
            {
                builder
                    .Append("CONSTRAINT ")
                    .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name.Substring(0, Math.Min(operation.Name.Length, 64))))
                    .Append(" ");
            }

            builder
                .Append("FOREIGN KEY (")
                .Append(ColumnList(operation.Columns))
                .Append(") REFERENCES ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.PrincipalTable, operation.PrincipalSchema));

            if (operation.PrincipalColumns != null)
            {
                builder
                    .Append(" (")
                    .Append(ColumnList(operation.PrincipalColumns))
                    .Append(")");
            }

            if (operation.OnUpdate != ReferentialAction.NoAction)
            {
                builder.Append(" ON UPDATE ");
                ForeignKeyAction(operation.OnUpdate, builder);
            }

            if (operation.OnDelete != ReferentialAction.NoAction)
            {
                builder.Append(" ON DELETE ");
                ForeignKeyAction(operation.OnDelete, builder);
            }
        }

	    protected override string ColumnList(string[] columns) => string.Join(", ", columns.Select(Dependencies.SqlGenerationHelper.DelimitIdentifier));
    }

    public static class StringExtensions
    {
        /// <summary>
        /// Method that limits the length of text to a defined length.
        /// </summary>
        /// <param name="source">The source text.</param>
        /// <param name="maxLength">The maximum limit of the string to return.</param>
        public static string LimitLength(this string source, int maxLength)
        {
            if (source.Length <= maxLength)
            {
                return source;
            }

            return source.Substring(0, maxLength);
        }
    }
}
