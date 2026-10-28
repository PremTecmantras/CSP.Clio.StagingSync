using CSP.Clio.API.Data;
using CSP.Clio.Sync.Services;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace CSP.Clio.Sync.Data
{
    public class BIDataManager
    {
        private int _maximumBatchCount = 50;
        private const int DefaultBatchCount = 50;
        private bool _updateData = false;
        public TimeSpan SqlWriteTime { get; private set; }

        public BIDataManager(bool updateData, int maximumBatchCount = DefaultBatchCount)
        {
            this.UpdateData = updateData;
            this.MaximumBatchCount = maximumBatchCount;
        }

        // EF wraps the SQL error two levels deep (DbUpdateException > UpdateException > SqlException).
        private static string GetInnermostMessage(Exception ex)
        {
            var inner = ex.InnerException;
            if (inner == null) return string.Empty;
            while (inner.InnerException != null) inner = inner.InnerException;
            return inner.Message;
        }

        public void LogException(Exception ex, string extraInfo)
        {

            using (var context = CreateContext())
            {
                context.ErrorInfoes.Add(new ErrorInfo()
                {
                    DateCreated = DateTime.Now,
                    ExtraInfo = extraInfo,
                    //InnerException = CSPSolutions.Exceptions.ExceptionFormatter.GetInnerException(ex),
                    InnerException = GetInnermostMessage(ex),
                    Message = ex.Message,
                    StackTrace = ex.StackTrace,
                    Type = ex.GetType().FullName

                });

                context.SaveChanges();
            }

        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="bills"></param>
        /// <param name="companyID"></param>
        /// <remarks>Field X is coming empoty</remarks>
        public void SaveBills(List<BillModelModel> bills, Guid companyID)
        {
            if (bills != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in bills select (int?)q.id).ToList();

                        var dataToUpdate = from p in context.Bills
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.Bills.Remove(item);
                        }

                        context.SaveChanges();
                    }

                    foreach (var bill in bills)
                    {
                        context.Bills.Add(new Bill()
                        {
                            ID = (long)bill.id,
                            Clio_Internal_ID = bill.id,
                            Bill_Theme = bill.bill_theme?.id,
                            ETag = bill.etag,
                            Created_At = bill.created_at.ToString(),
                            Updated_At = bill.updated_at.ToString(),
                            JSON = JsonConvert.SerializeObject(bill),
                            Redacted = 0,
                            Balance = bill.balance.ToString(),
                            Can_Update = Convert.ToInt64(bill.can_update),
                            Client = Convert.ToInt64(bill.client?.id),
                            Credits_Issued = bill.credits_issued.ToString(),
                            Currency = bill.currency.id,
                            Destination_Account = Convert.ToInt64(bill.destination_account?.id),
                            Due = bill.due.ToString(),
                            Due_At = bill.due_at,
                            End_At = bill.end_at,
                            Group = Convert.ToInt64(bill.group?.id),
                            Issued_At = bill.issued_at,
                            Kind = GetBillKind(bill.kind),//Enum 
                            Memo = CleanStringRemoveArabic(bill.memo, true),
                            Number = bill.number,
                            Original_Bill = bill.original?.id,
                            Paid = bill.paid.ToString(),
                            Paid_At = bill.paid_at,
                            Purchase_Order = bill.purchase_order?.ToString(),
                            Secondary_Tax_Rate = bill.secondary_tax_rate.ToString(),
                            Secondary_Tax_Sum = bill.secondary_tax_sum.ToString(),
                            Shared = Convert.ToInt64(bill.shared),
                            Start_At = bill.start_at,
                            State = GetBillState(bill.state),
                            Sub_Total = bill.sub_total.ToString(),
                            Subject = bill.subject?.ToString(),
                            Tax_Rate = bill.tax_rate.ToString(),
                            Tax_Sum = bill.tax_sum.ToString(),
                            Total = bill.total.ToString(),
                            Type = GetBillType(bill.type),
                            User = Convert.ToInt64(bill.user?.id),
                            CompanyID = companyID,

                        });


                    }

                    if (!_updateData)
                    {
                        BulkInsertEntities<Bill>(context, "Bills", "BulkBatchSize.Bills");
                        return;
                    }

                    SaveChangesForTiming(context);
                }
            }
        }

        public long GetBillType(string typeName)
        {
            long type = 1;

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                if (string.Compare(typeName, "draft", true) == 0 || typeName.ToLower().IndexOf("draft") != -1)
                {
                    type = 1;
                }
                else if (string.Compare(typeName, "awaiting_approval", true) == 0 || typeName.ToLower().IndexOf("awaiting_approval") != -1)
                {
                    type = 2;
                }
                else if (string.Compare(typeName, "awaiting_payment", true) == 0 || typeName.ToLower().IndexOf("awaiting_payment") != -1)
                {
                    type = 3;
                }
                else if (string.Compare(typeName, "paid", true) == 0 || typeName.ToLower().IndexOf("paid") != -1)
                {
                    type = 4;
                }
                else if (string.Compare(typeName, "paid", true) == 0 || typeName.ToLower().IndexOf("void") != -1)
                {
                    type = 5;
                }
                else if (string.Compare(typeName, "deleted", true) == 0 || typeName.ToLower().IndexOf("deleted") != -1)
                {
                    type = 6;
                }
                else if (string.Compare(typeName, "matterbill", true) == 0 || typeName.ToLower().IndexOf("matterbill") != -1)
                {
                    type = 7;
                }
                else if (string.Compare(typeName, "clientbill", true) == 0 || typeName.ToLower().IndexOf("clientbill") != -1)
                {
                    type = 8;
                }
                else
                {
                    _dictionaryMissingValues["GetBillType" + typeName] = typeName;
                    type = 1;
                }
            }

            return type;
        }

        public void SaveFolders(List<FolderModel> data, Guid companyID)
        {
            if (data != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in data select (int?)q.id).ToList();

                        var dataToUpdate = from p in context.Folders
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.Folders.Remove(item);
                        }

                        context.SaveChanges();
                    }

                    foreach (var item in data)
                    {
                        context.Folders.Add(new Folder()
                        {
                            ID = Convert.ToInt64(item.id),
                            Clio_Internal_ID = item.id,
                            CompanyID = companyID,
                            Created_At = item.created_at.ToString(),
                            Updated_At = item.updated_at.ToString(),
                            JSON = JsonConvert.SerializeObject(item),
                            Redacted = 0,
                            ETag = item.etag,
                            Creator = (long?)item.creator?.id,
                            Deleted_At = string.Empty,
                            Name = item.name,
                            Contact = item.contact?.id,
                            Document_Category = item.document_category?.id,
                            Locked = item.locked ? 1 : 0,
                            Parent = item.parent?.id,
                            Root = item.root ? 1 : 0,
                            Matter = (long?)item.matter?.id,
                            Type = item.type
                        });
                    }

                    SaveChangesForTiming(context);
                }

            }
        }

        public void SaveRelationship(List<RelationshipDataModel> data, Guid companyID)
        {
            if (data != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in data select (int?)q.id).ToList();

                        var dataToUpdate = from p in context.Relationships
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.Relationships.Remove(item);
                        }

                        context.SaveChanges();
                    }

                    foreach (var item in data)
                    {
                        context.Relationships.Add(new Relationship()
                        {
                            ID = Convert.ToInt64(item.id),

                            Clio_Internal_ID = item.id,
                            CompanyID = companyID,

                            Created_At = item.created_at.ToString(),
                            Updated_At = item.updated_at.ToString(),
                            JSON = JsonConvert.SerializeObject(item),
                            Redacted = item.redacted ? 1 : 0,
                            ETag = item.etag,

                            Description = item.description,

                            Contact = item.contact?.id,
                            Matter = (long?)item.matter?.id,

                        });
                    }

                    SaveChangesForTiming(context);
                }

            }
        }

        private void BulkInsertEntities<TEntity>(EKPBIEntities context, string tableName, string batchSizeSetting) where TEntity : class
        {
            var entities = context.Set<TEntity>().Local.ToList();
            if (entities.Count == 0)
                return;

            var properties = typeof(TEntity).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.CanRead && property.Name != "ID")
                .ToArray();
            var table = new DataTable();
            foreach (var property in properties)
            {
                var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                table.Columns.Add(property.Name, propertyType);
            }

            foreach (var entity in entities)
            {
                var row = table.NewRow();
                foreach (var property in properties)
                    row[property.Name] = property.GetValue(entity, null) ?? DBNull.Value;
                table.Rows.Add(row);
            }

            var configuredBatchSize = System.Configuration.ConfigurationManager.AppSettings[batchSizeSetting];
            int batchSize;
            if (string.IsNullOrWhiteSpace(configuredBatchSize))
                batchSize = 1000;
            else if (!int.TryParse(configuredBatchSize, out batchSize) || batchSize <= 0)
                throw new InvalidOperationException("AppSetting '" + batchSizeSetting + "' must be a positive integer.");

            var timer = Stopwatch.StartNew();
            try
            {
                var connection = (SqlConnection)context.Database.Connection;
                if (connection.State != ConnectionState.Open)
                    connection.Open();

                using (var bulkCopy = new SqlBulkCopy(connection))
                {
                    bulkCopy.DestinationTableName = "[dbo].[" + tableName + "]";
                    bulkCopy.BatchSize = batchSize;
                    bulkCopy.BulkCopyTimeout = 360;
                    foreach (var property in properties)
                        bulkCopy.ColumnMappings.Add(property.Name, property.Name);
                    bulkCopy.WriteToServer(table);
                }
            }
            finally
            {
                timer.Stop();
                SqlWriteTime += timer.Elapsed;
            }
        }

        public void SaveLineItems(List<LineItemModel> data, Guid companyID)
        {
            if (data != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in data select (int?)q.id).ToList();

                        var dataToUpdate = from p in context.LineItems
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.LineItems.Remove(item);
                        }

                        context.SaveChanges();
                    }

                    foreach (var item in data)
                    {
                        context.LineItems.Add(new LineItem()
                        {
                            ID = Convert.ToInt64(item.id),
                            Clio_Internal_ID = item.id,
                            CompanyID = companyID,
                            Created_At = item.created_at.ToString(),
                            Updated_At = item.updated_at.ToString(),
                            JSON = JsonConvert.SerializeObject(item),
                            Redacted = 0,
                            Tax = item.tax.ToString(),

                            Taxable = item.taxable ? 1 : 0,
                            Note = item.note,
                            Total = item.total.ToString(),
                            ETag = item.etag,
                            User = (long?)item.user?.id,
                            Date = item.date,
                            Description = item.description,
                            Quantity = item.quantity.ToString(),

                            Sub_Total = item.sub_total.ToString(),
                            Matter = (long?)item.matter?.id,
                            Bill = (long?)item.bill?.id,
                            Activity = (long?)item.activity?.id,
                            Price = item.price.ToString(),

                            Secondary_Taxable = item.secondary_taxable ? 1 : 0,
                            Group_Ordering = item.group_ordering,
                            Secondary_Tax = item.Secondary_Tax,
                            Kind = item.kind,
                            Type = item.type,

                        });
                    }

                    if (!_updateData)
                    {
                        BulkInsertEntities<LineItem>(context, "LineItems", "BulkBatchSize.LineItems");
                        return;
                    }

                    SaveChangesForTiming(context);
                }

            }
        }

        public void SaveTasks(List<TaskDataModel> data, Guid companyID)
        {
            if (data != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in data select (int?)q.id).ToList();

                        var dataToUpdate = from p in context.Tasks
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.Tasks.Remove(item);
                        }

                        context.SaveChanges();
                    }

                    foreach (var item in data)
                    {
                        context.Tasks.Add(new Task()
                        {
                            ID = Convert.ToInt64(item.id),
                            Clio_Internal_ID = item.id,
                            CompanyID = companyID,
                            Created_At = item.created_at.ToString(),
                            Updated_At = item.updated_at.ToString(),
                            JSON = JsonConvert.SerializeObject(item),
                            Redacted = 0,
                            Description = item.description,
                            Name = item.name,
                            ETag = item.etag,
                            Assignee = item?.assignee?.id,
                            Assigner = item?.assigner?.id,
                            Completed_At = item.completed_at?.ToString(),
                            Due_At = item.due_at,
                            Notify_Completion = item.notify_completion ? 1 : 0,
                            Priority = GetTaskPriority(item.priority), //update
                            Status = GetTaskStatus(item.status), // update
                            Statute_Of_Limitations = item.statute_of_limitations ? 1 : 0,
                            Task_Type = item?.task_type?.id,

                            Matter = (long?)item.matter?.id,


                        });
                    }

                    if (!_updateData)
                    {
                        BulkInsertEntities<Task>(context, "Tasks", "BulkBatchSize.Tasks");
                        return;
                    }

                    SaveChangesForTiming(context);
                }

            }
        }

        public void SaveTaskTypes(List<TaskTypeDataModel> data, Guid companyID)
        {
            if (data != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in data select (int?)q.id).ToList();

                        var dataToUpdate = from p in context.TaskTypes
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.TaskTypes.Remove(item);
                        }

                        context.SaveChanges();
                    }

                    foreach (var item in data)
                    {
                        context.TaskTypes.Add(new TaskType()
                        {
                            ID = Convert.ToInt64(item.id),
                            Clio_Internal_ID = item.id,
                            CompanyID = companyID,
                            Created_At = item.created_at.ToString(),
                            Updated_At = item.updated_at.ToString(),
                            JSON = JsonConvert.SerializeObject(item),
                            Redacted = 0,
                            Name = item.name,
                            ETag = item.etag
                        });
                    }

                    SaveChangesForTiming(context);
                }

            }
        }

        internal void SaveMatterNotes(List<NoteDataModel> data, Guid companyID)
        {
            if (data != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in data select (int?)q.id).ToList();

                        var dataToUpdate = from p in context.MatterNotes
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.MatterNotes.Remove(item);
                        }

                        context.SaveChanges();
                    }

                    foreach (var item in data)
                    {
                        context.MatterNotes.Add(new MatterNote()
                        {
                            ID = Convert.ToInt64(item.id),
                            Clio_Internal_ID = item.id,
                            CompanyID = companyID,
                            Created_At = item.created_at.ToString(),
                            Updated_At = item.updated_at.ToString(),
                            JSON = JsonConvert.SerializeObject(item),
                            Redacted = 0,
                            ETag = item.etag,
                            Matter = (long?)item.matter?.id,
                            Author = item.author?.id,
                            Contact = item.contact?.id,
                            Date = item.date,
                            Detail = item.detail,
                            Subject = item.subject
                        });
                    }

                    if (!_updateData)
                    {
                        BulkInsertEntities<MatterNote>(context, "MatterNotes", "BulkBatchSize.MatterNotes");
                        return;
                    }

                    SaveChangesForTiming(context);
                }

            }
        }

        public long GetTaskStatus(string typeName)
        {
            long type = 1;

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                if (string.Compare(typeName, "pending", true) == 0 || typeName.ToLower().IndexOf("pending") != -1)
                {
                    type = 1;
                }
                else if (string.Compare(typeName, "in_progress", true) == 0 || typeName.ToLower().IndexOf("in_progress") != -1)
                {
                    type = 2;
                }
                else if (string.Compare(typeName, "in_review", true) == 0 || typeName.ToLower().IndexOf("in_review") != -1)
                {
                    type = 3;
                }
                else if (string.Compare(typeName, "complete", true) == 0 || typeName.ToLower().IndexOf("complete") != -1)
                {
                    type = 4;
                }

            }

            return type;
        }

        public long GetTaskPriority(string typeName)
        {
            long type = 2;

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                if (string.Compare(typeName, "high", true) == 0 || typeName.ToLower().IndexOf("high") != -1)
                {
                    type = 1;
                }
                else if (string.Compare(typeName, "normal", true) == 0 || typeName.ToLower().IndexOf("normal") != -1)
                {
                    type = 2;
                }
                else if (string.Compare(typeName, "low", true) == 0 || typeName.ToLower().IndexOf("low") != -1)
                {
                    type = 3;
                }

            }

            return type;
        }

        Dictionary<string, string> _dictionaryMissingValues = new Dictionary<string, string>();

        public bool UpdateData { get => _updateData; set => _updateData = value; }
        public int MaximumBatchCount { get => _maximumBatchCount; set => _maximumBatchCount = value; }

        public long GetBillKind(string typeName)
        {
            long type = 4;

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                if (string.Compare(typeName, "revenue", true) == 0 || typeName.ToLower().IndexOf("revenue") != -1)
                {
                    type = 1;
                }
                else if (string.Compare(typeName, "aggregate", true) == 0 || typeName.ToLower().IndexOf("aggregate") != -1)
                {
                    type = 4;
                }
                else if (string.Compare(typeName, "trust_kind", true) == 0 || typeName.ToLower().IndexOf("trust_kind") != -1)
                {
                    type = 5;
                }
                else
                {
                    _dictionaryMissingValues["GetBillKind" + typeName] = typeName;
                    type = 4;
                }
            }

            return type;
        }


        public long GetBillState(string typeName)
        {
            long type = 1;

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                if (string.Compare(typeName, "draft", true) == 0 || typeName.ToLower().IndexOf("draft") != -1)
                {
                    type = 1;
                }
                else if (string.Compare(typeName, "state2", true) == 0 || typeName.ToLower().IndexOf("state2") != -1)
                {
                    type = 2;
                }
                else if (string.Compare(typeName, "awaiting_payment", true) == 0 || typeName.ToLower().IndexOf("awaiting_payment") != -1)
                {
                    type = 3;
                }
                else if (string.Compare(typeName, "paid", true) == 0 || typeName.ToLower().IndexOf("paid") != -1)
                {
                    type = 4;
                }
                else if (string.Compare(typeName, "void", true) == 0 || typeName.ToLower().IndexOf("void") != -1)
                {
                    type = 5;
                }
                else if (string.Compare(typeName, "deleted", true) == 0 || typeName.ToLower().IndexOf("deleted") != -1)
                {
                    type = 6;
                }
                else if (string.Compare(typeName, "state7", true) == 0 || typeName.ToLower().IndexOf("state7") != -1)
                {
                    type = 7;
                }
                else
                {
                    _dictionaryMissingValues["GetBillState" + typeName] = typeName;
                    type = 1;
                }
            }

            return type;
        }


        public EKPBIEntities CreateContext()
        {
            EKPBIEntities context = new EKPBIEntities();

            context.Database.CommandTimeout = 360;

            // context.Configuration.ValidateOnSaveEnabled = false;

            return context;
        }

        private EKPBIEntities CreateSaveContext()
        {
            var context = CreateContext();
            context.Configuration.AutoDetectChangesEnabled = false;
            return context;
        }

        private void SaveChangesForTiming(EKPBIEntities context)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                context.SaveChanges();
            }
            finally
            {
                timer.Stop();
                SqlWriteTime += timer.Elapsed;
            }
        }

        public EKPBIEntities CreateContextFoSP()
        {
            EKPBIEntities context = new EKPBIEntities();

            context.Database.CommandTimeout = 0;

            return context;
        }

        public void SaveMatters(List<MatterDataModel> matters, Guid companyID)
        {
            if (matters != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in matters select (int?)q.id).ToList();

                        var dataToUpdate = from p in context.Matters
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.Matters.Remove(item);
                        }

                        context.SaveChanges();
                    }

                    foreach (var matter in matters)
                    {
                        context.Matters.Add(new Matter()
                        {
                            ID = Convert.ToInt64(matter.id),
                            Clio_Internal_ID = matter.id,
                            Location = matter.location,
                            ETag = matter.etag,
                            Created_At = matter.created_at.ToString(),
                            Updated_At = matter.updated_at.ToString(),
                            JSON = JsonConvert.SerializeObject(matter),
                            Redacted = 0,
                            Billable = Convert.ToInt64(matter.billable),
                            Billing_Method = GetBillingMethods(matter.billing_method),
                            Client = Convert.ToInt64(matter.client?.id),
                            Client_Reference = matter.client_reference?.ToString(),
                            Close_Date = matter.close_date?.ToString(),
                            Contingency_Fee = 0,//Convert.ToInt64(matter.contingency_fee.),
                            Custom_Rate_Type = matter.custom_rate?.type,
                            Custom_Rate_Rates = matter.custom_rate?.rates != null ? JsonConvert.SerializeObject(matter.custom_rate.rates) : null,
                            Custom_Number = matter.custom_number?.ToString(),
                            Description = CleanStringRemoveArabic(matter.description, true),
                            Display_Number = CleanStringRemoveArabic(matter.display_number, false),
                            Group = Convert.ToInt64(matter.group?.id),
                            Folder = Convert.ToInt64(matter.folder?.id),
                            Maildrop_Address = matter.maildrop_address,
                            Open_Date = matter.open_date.ToString(),
                            Pending_Date = matter.pending_date?.ToString(),
                            Practice_Area = Convert.ToInt64(matter.practice_area?.id),
                            Responsible_Attorney = Convert.ToInt64(matter.responsible_attorney?.id),
                            Status = GetMatterStatus(matter.status),
                            CompanyID = companyID,

                        });
                    }

                    if (!_updateData)
                    {
                        BulkInsertEntities<Matter>(context, "Matters", "BulkBatchSize.Matters");
                        return;
                    }

                    SaveChangesForTiming(context);
                }
            }
        }

        public void SaveContacts(List<ContactDataModel> contacts, Guid companyID)
        {
            if (contacts != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in contacts select (int?)q.id).ToList();

                        var dataToUpdate = from p in context.Contacts
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.Contacts.Remove(item);
                        }

                        context.SaveChanges();
                    }

                    foreach (var contact in contacts)
                    {
                        context.Contacts.Add(new Contact()
                        {
                            ID = Convert.ToInt64(contact.id),
                            Clio_Internal_ID = contact.id,
                            CompanyID = companyID,
                            Name = contact.name,
                            Clio_Connect_Email = contact.clio_connect_email?.ToString(),
                            Company = Convert.ToInt64(contact.company),
                            Avatar = Convert.ToInt64(contact.avatar),
                            Created_At = contact.created_at.ToString(),
                            ETag = contact.etag,
                            First_Name = contact.first_name,
                            Folder = Convert.ToInt64(contact.folder),
                            Initials = contact.initials,
                            JSON = JsonConvert.SerializeObject(contact),
                            Last_Name = contact.last_name,
                            Middle_Name = contact.middle_name,
                            Payment_Profile = Convert.ToInt64(contact.payment_profile?.id),
                            Prefix = contact.prefix,
                            Primary_Email_Address = contact.primary_email_address,
                            Primary_Phone_Number = contact.primary_phone_number,
                            Redacted = 0,
                            Title = contact.title,
                            Type = GetContactType(contact.type),
                            ContactType = contact.type,
                            Updated_At = contact.updated_at.ToString(),
                            Co_Counsel_Rate = contact.co_counsel_rate?.rate
                        });
                    }

                    if (!_updateData)
                    {
                        BulkInsertEntities<Contact>(context, "Contacts", "BulkBatchSize.Contacts");
                        return;
                    }

                    SaveChangesForTiming(context);
                }
            }
        }
        public int GetContactType(string contactTypeName)
        {
            int contactType = 2; // person

            if (!string.IsNullOrWhiteSpace(contactTypeName))
            {
                if (contactTypeName.ToLower().IndexOf("person") != -1)
                {
                    contactType = 2; // person
                }
                else if (contactTypeName.ToLower().IndexOf("company") != -1)
                {
                    contactType = 1; // company
                }
            }

            return contactType;
        }


        public int GetMatterStatus(string value)
        {
            int result = 1;

            if (!string.IsNullOrWhiteSpace(value))
            {
                if (value.ToLower().IndexOf("open") != -1)
                {
                    result = 2;
                }
                else if (value.ToLower().IndexOf("closed") != -1)
                {
                    result = 3;
                }
                else if (value.ToLower().IndexOf("pending") != -1)
                {
                    result = 1;
                }
            }

            return result;
        }

        public int GetBillingMethods(string value)
        {
            int result = 1;

            if (!string.IsNullOrWhiteSpace(value))
            {
                if (value.ToLower().IndexOf("flat") != -1)
                {
                    result = 1;
                }
                else if (value.ToLower().IndexOf("hourly") != -1)
                {
                    result = 2;
                }
                else if (value.ToLower().IndexOf("contingency") != -1)
                {
                    result = 3;
                }
            }

            return result;
        }




        public void SaveActivity(List<ActivityData> activities, Guid companyID)
        {

            if (activities != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in activities select (int?)q.id).ToList();

                        var dataToUpdate = from p in context.Activities
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.Activities.Remove(item);
                        }

                        context.SaveChanges();
                    }


                    foreach (var activity in activities)
                    {
                        context.Activities.Add(new Activity()
                        {

                            ID = Convert.ToInt64(activity.id.ToString()),
                            Clio_Internal_ID = activity.id,
                            CompanyID = companyID,

                            Type = this.GetActivityType(activity.type), // Database entry enum
                            Updated_At = activity.updated_at.ToString(),
                            Activity_Description = (long?)(activity.activity_description?.id),
                            Redacted = 0,
                            Bill = (long?)(activity.bill?.id),
                            Billed = Convert.ToInt64(activity.billed),
                            Calendar_Entry = (long?)(activity.calendar_entry?.id),
                            Communication = (long?)(activity.communication?.id),
                            Contact_Note = (long?)(activity.contact_note?.id),
                            Contingency_Fee = Convert.ToInt64(activity.contingency_fee),
                            Created_At = activity.created_at.ToString(),
                            Date = activity.date?.ToString(),
                            ETag = activity.etag,
                            Expense_Category = null,
                            Flat_Rate = Convert.ToInt64(activity.flat_rate),
                            JSON = JsonConvert.SerializeObject(activity),
                            Matter = Convert.ToInt64(activity.matter?.id),
                            Matter_Note = Convert.ToInt64(activity.matter_note?.id),
                            Non_Billable = Convert.ToInt64(activity.non_billable),
                            Note = CleanStringRemoveArabic(activity.note, true),
                            Price = activity.price.ToString(),
                            Quantity = activity.quantity.ToString(),
                            Quantity_In_Hours = activity.quantity_in_hours?.ToString(),
                            Reference = activity.reference,
                            Rounded_Quantity = activity.rounded_quantity?.ToString(),
                            Rounded_Quantity_In_Hours = activity.rounded_quantity_in_hours?.ToString(),
                            Subject = Convert.ToInt64(activity.subject?.id),
                            Timer = Convert.ToInt64(activity.timer?.id),
                            Total = activity.total.ToString(),
                            User = Convert.ToInt64(activity.user?.id),
                            Utbms_Expense = Convert.ToInt64(activity.utbms_expense?.id),
                            Vendor = Convert.ToInt64(activity.vendor?.id),
                            UserTimesheet_key = this.CreateUserTimesheetKey(Convert.ToInt64(activity.matter?.id), Convert.ToInt64(activity.user?.id), activity.date?.ToString())
                        });
                    }

                    if (!_updateData)
                    {
                        BulkInsertEntities<Activity>(context, "Activities", "BulkBatchSize.Activities");
                        return;
                    }

                    SaveChangesForTiming(context);

                }
            }
        }



        private decimal? CreateUserTimesheetKey(long matterId, long userId, string date)
        {
            decimal? result = null;
            DateTime activityDate;
            string temp;

            if (DateTime.TryParse(date, out activityDate))
            {
                var dateNumber = string.Format("{0}{1}{2}", activityDate.Year, activityDate.Month.ToString("D2"), activityDate.Day.ToString("D2"));

                temp = string.Format("{0}{1}{2}", matterId, userId, dateNumber);

                result = decimal.Parse(temp);
            }

            return result;
        }

        public void ClearData(Guid companyID)
        {
            try
            {

                using (var context = CreateContext())
                {
                    var objectContext = (context as System.Data.Entity.Infrastructure.IObjectContextAdapter).ObjectContext;
                    objectContext.CommandTimeout = (int)TimeSpan.FromSeconds(60 * 10).TotalSeconds;

                    context.SP_ClearData(companyID);
                }

                Console.WriteLine(string.Format(Properties.Resources.ClearCompanyData));
                Console.WriteLine();

            }
            catch (Exception ex)
            {
                this.LogException(ex, "ClearData");
            }

        }

        public void ClearAllData()
        {
            using (var context = CreateContext())
            {
                var objectContext = (context as System.Data.Entity.Infrastructure.IObjectContextAdapter).ObjectContext;
                objectContext.CommandTimeout = (int)TimeSpan.FromMinutes(10).TotalSeconds;
                context.SP_ClearAllData();
            }
        }

        public void CleanData()
        {

            using (var context = CreateContext())
            {
                context.SP_CleanData();
            }

            Console.WriteLine(string.Format(Properties.Resources.CleaningData));
            Console.WriteLine();
        }

        /// <summary>
        /// Clean and remove Arabic
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public string CleanStringRemoveArabic(string value, bool removeSymbols)
        {
            string result = value;

            if (!string.IsNullOrWhiteSpace(value))
            {
                var data = System.Text.ASCIIEncoding.ASCII.GetBytes(value);

                value = System.Text.ASCIIEncoding.ASCII.GetString(data);

                result = value.Replace("?", string.Empty);

                if (removeSymbols)
                {
                    result = result.Replace("% ", string.Empty);
                    result = result.Replace(":", string.Empty);
                    result = result.Replace("/", string.Empty);
                    result = result.Replace("-", string.Empty);
                    result = result.Replace("_", string.Empty);
                    result = result.Replace("–", string.Empty);
                }

                result = result.Trim();
            }

            return result;
        }

        public long GetActivityType(string typeName)
        {
            long type = 2;

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                if (typeName.ToLower().IndexOf("time") != -1)
                {
                    type = 1;
                }
                else if (typeName.ToLower().IndexOf("expense") != -1)
                {
                    type = 2;
                }
            }

            return type;
        }

        public void SaveCustomFields(List<CustomFieldData> customfields, Guid companyID)
        {
            if (customfields != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in customfields select (int?)int.Parse(q.id)).ToList();

                        var dataToUpdate = from p in context.CustomFields
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.CustomFields.Remove(item);
                        }

                        context.SaveChanges();
                    }


                    foreach (var cfield in customfields)
                    {
                        context.CustomFields.Add(new CustomField()
                        {

                            ID = Convert.ToInt64(cfield.id),
                            Clio_Internal_ID = int.Parse(cfield.id),
                            CompanyID = companyID,
                            Created_At = cfield.created_at.ToString(),
                            Updated_At = cfield.updated_at.ToString(),
                            JSON = JsonConvert.SerializeObject(cfield),
                            Redacted = 0,
                            Deleted = Convert.ToInt64(cfield.deleted),
                            Displayed = Convert.ToInt64(cfield.displayed),
                            Display_Order = cfield.display_order,
                            ETag = cfield.etag,
                            Field_Type = Convert.ToInt64(cfield.field_type),
                            Name = cfield.name,
                            Parent_Type = 0, //Convert.ToInt64(cfield.parent_type), Enum
                            Required = Convert.ToInt64(cfield.required),
                        });
                    }

                    SaveChangesForTiming(context);
                }
            }
        }


        public void SaveBankTransactions(List<BankTransactionModel> data, Guid companyID)
        {
            if (data != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in data select (int?)q.id).ToList();

                        var dataToUpdate = from p in context.BankTransactions
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.BankTransactions.Remove(item);
                        }

                        context.SaveChanges();
                    }


                    foreach (var record in data)
                    {
                        long payTrans = bool.Parse(record.clio_payments_transaction) ? 1 : 0;

                        context.BankTransactions.Add(new BankTransaction()
                        {
                            ID = record.id,
                            Clio_Internal_ID = record.id,
                            Allocation = record.allocation?.id,
                            Amount = record.amount.ToString(),
                            Bank_Account = record.bank_account?.id,
                            Bill = record.bill?.id,
                            Client = record.client?.id,
                            Clio_Payments_Transaction = payTrans,
                            Confirmation = record.confirmation,
                            Created_At = record.created_at,
                            Currency = record.currency,
                            Date = record.date,
                            Description = record.description,
                            ETag = record.etag,
                            Exchange_Rate = record.exchange_rate,
                            Funds_In = record.funds_in,
                            Funds_Out = record.funds_out,
                            JSON = JsonConvert.SerializeObject(record),
                            Matter = record?.matter?.id,
                            Redacted = record.redacted,
                            Source = record.source,
                            Transaction_Type = record.transaction_type,
                            Type = record?.type.IndexOf("ass") != -1 ? 1 : 0,
                            Updated_At = record.updated_at,
                            CompanyID = companyID
                        });
                    }

                    if (!_updateData)
                    {
                        BulkInsertEntities<BankTransaction>(context, "BankTransactions", "BulkBatchSize.BankTransactions");
                        return;
                    }

                    SaveChangesForTiming(context);
                }
            }
        }

        public void SavePracticeAreas(List<PracticeAreaData> practiceAreas, Guid companyID)
        {
            if (practiceAreas != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in practiceAreas select (int?)q.id).ToList();

                        var dataToUpdate = from p in context.PracticeAreas
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.PracticeAreas.Remove(item);
                        }

                        context.SaveChanges();
                    }


                    foreach (var practiceArea in practiceAreas)
                    {
                        context.PracticeAreas.Add(new PracticeArea()
                        {

                            ID = Convert.ToInt64(practiceArea.id),
                            Clio_Internal_ID = practiceArea.id,
                            Created_At = practiceArea.created_at.ToString(),
                            Updated_At = practiceArea.updated_at.ToString(),
                            ETag = practiceArea.etag,
                            Name = practiceArea.name,
                            Code = practiceArea.code,
                            JSON = JsonConvert.SerializeObject(practiceArea),
                            CompanyID = companyID

                        });
                    }

                    SaveChangesForTiming(context);
                }
            }
        }

        public void SaveActivityDescription(List<ActivityDescriptionModel> activityDescriptions, Guid companyID)
        {
            if (activityDescriptions != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in activityDescriptions select (int?)q.id).ToList();

                        var dataToUpdate = from p in context.ActivityDescriptions
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.ActivityDescriptions.Remove(item);
                        }

                        context.SaveChanges();
                    }

                    foreach (var activityDescription in activityDescriptions)
                    {
                        context.ActivityDescriptions.Add(new ActivityDescription()
                        {

                            ID = Convert.ToInt64(activityDescription.id),
                            Clio_Internal_ID = activityDescription.id,
                            Created_At = activityDescription.created_at.ToString(),
                            Updated_At = activityDescription.updated_at.ToString(),
                            ETag = activityDescription.etag,
                            Name = activityDescription.name,
                            JSON = JsonConvert.SerializeObject(activityDescription),
                            Default = Convert.ToInt64(activityDescription.@default),
                            Redacted = 0,
                            Accessible_To_User = Convert.ToInt64(activityDescription.accessible_to_user),
                            Utbms_Activity = null,
                            Utbms_Activity_Id = activityDescription.utbms_activity?.id,
                            Utbms_Task = null,
                            Utbms_Task_Id = activityDescription.utbms_task_id,
                            Visible_To_Co_Counsel = Convert.ToInt64(activityDescription.visible_to_co_counsel),
                            CompanyID = companyID


                        });
                    }

                    SaveChangesForTiming(context);
                }
            }
        }

        public void SaveCustomFieldSets(List<CustomFieldSets> customFieldSets, Guid companyID)
        {
            if (customFieldSets != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in customFieldSets select (int?)q.id).ToList();

                        var dataToUpdate = from p in context.CustomFieldSets
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.CustomFieldSets.Remove(item);
                        }

                        context.SaveChanges();
                    }

                    foreach (var customFieldSet in customFieldSets)
                    {
                        context.CustomFieldSets.Add(new CustomFieldSet()
                        {
                            ID = Convert.ToInt64(customFieldSet.id),
                            Clio_Internal_ID = customFieldSet.id,
                            Created_At = customFieldSet.created_at.ToString(),
                            Updated_At = customFieldSet.updated_at.ToString(),
                            ETag = customFieldSet.etag,
                            Name = customFieldSet.name,
                            JSON = JsonConvert.SerializeObject(customFieldSet),
                            Redacted = 0,
                            Displayed = Convert.ToInt64(customFieldSet.displayed),
                            Parent_Type = GetCustomFieldTypes(customFieldSet.parent_type),
                            CompanyID = companyID

                        });
                    }

                    SaveChangesForTiming(context);
                }
            }
        }

        public long GetCustomFieldTypes(string typeName)
        {
            long type = 1;

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                if (typeName.ToLower().IndexOf("matter") != -1)
                {
                    type = 2;
                }
                else if (typeName.ToLower().IndexOf("contact") != -1)
                {
                    type = 1;
                }
            }

            return type;
        }

        public void SaveAllocations(List<AllocationsModel> allocations, Guid companyID)
        {

            using (var context = CreateSaveContext())
            {
                if (_updateData)
                {
                    List<int?> filter = (from q in allocations select (int?)q.id).ToList();

                    var dataToUpdate = from p in context.Allocations
                                       where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                       select p;

                    foreach (var item in dataToUpdate)
                    {
                        context.Allocations.Remove(item);
                    }

                    context.SaveChanges();
                }

                foreach (var allocation in allocations)
                {
                    context.Allocations.Add(new Allocation()
                    {

                        ID = Convert.ToInt64(allocation.id),
                        Clio_Internal_ID = allocation.id,
                        Created_At = allocation.created_at.ToString(),
                        Updated_At = allocation.updated_at.ToString(),
                        ETag = allocation.etag,
                        JSON = JsonConvert.SerializeObject(allocation),
                        Redacted = 0,
                        Amount = allocation.amount?.ToString(),
                        Date = allocation.date?.ToString(),
                        Description = allocation.description?.ToString(),
                        Destination_Bank_Account = allocation.destination_bank_account?.id,
                        Interest = Convert.ToInt64(allocation.interest),
                        Has_Online_Payment = Convert.ToInt64(allocation.has_online_payment),
                        Source_Bank_Account = Convert.ToInt64(allocation.source_bank_account?.id),
                        Contact = Convert.ToInt64(allocation.contact?.id),
                        Bill = Convert.ToInt64(allocation.bill?.id),
                        Matter = Convert.ToInt64(allocation.matter?.id),
                        Parent = Convert.ToInt64(allocation.parent?.id),
                        Voided_At = allocation.voided_at?.ToString(),
                        CompanyID = companyID
                    });
                }

                if (!_updateData)
                {
                    BulkInsertEntities<Allocation>(context, "Allocations", "BulkBatchSize.Allocations");
                    return;
                }

                SaveChangesForTiming(context);
            }

        }
        public void SaveUsers(List<UserDataModel> users, Guid companyID)
        {
            if (users != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in users select (int?)q.id).ToList();

                        var dataToUpdate = from p in context.Users
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.Users.Remove(item);
                        }

                        context.SaveChanges();
                    }

                    foreach (var user in users)
                    {
                        context.Users.Add(new User()
                        {

                            ID = Convert.ToInt64(user.id),
                            Clio_Internal_ID = user.id,
                            ETag = user.etag,
                            Redacted = 0,
                            Account = (long?)user.account?.id,
                            Account_Owner = Convert.ToInt64(user.account_owner),
                            Avatar = (long?)user.avatar?.id,
                            Clio_Connect = Convert.ToInt64(user.clio_connect),
                            CompanyID = companyID,
                            Court_Rules_Default_Attendee = Convert.ToInt64(user.court_rules_default_attendee),
                            Created_At = user.created_at.ToString(),
                            Updated_At = user.updated_at.ToString(),
                            Default_Activity_Description = (long?)user.default_activity_description?.id,
                            Default_Calendar_Id = (long)(user.default_calendar_id.HasValue ? user.default_calendar_id.Value : 0),
                            EMail = user.email,
                            Enabled = Convert.ToInt64(user.enabled),
                            First_Name = CleanStringRemoveArabic(user.first_name, false),
                            Last_Name = CleanStringRemoveArabic(user.last_name, false),
                            Initials = CleanStringRemoveArabic(user.initials, false),
                            IsWhoAmI = 0,
                            Job_Title = (long?)user.job_title?.id,
                            JSON = JsonConvert.SerializeObject(user),
                            Name = CleanStringRemoveArabic(user.name, false),
                            Phone_Number = user.phone_number,
                            Rate = user.rate.ToString(),
                            Subscription_Type = GetContactType(user.subscription_type),
                            Time_Zone = user.time_zone,


                        });
                    }

                    SaveChangesForTiming(context);
                }
            }
        }

        public List<C_EKPCompanies> GetClioConnectedCompanies()
        {
            List<C_EKPCompanies> result = null;

            using (var context = CreateContext())
            {
                var query = from p in context.C_EKPCompanies

                            where p.Active == true
                            select p;

                result = query.ToList();
            }

            foreach (var item in result)
            {
                item.ClioApiKey = item.ClioApiKey?.Trim();
                item.ClioApiSecret = item.ClioApiSecret?.Trim();
                item.ClioRefreshToken = item.ClioRefreshToken?.Trim();
                item.ClioToken = item.ClioToken?.Trim();
                item.ClioAPIURL = item.ClioAPIURL?.Trim();
            }

            return result;
        }


        public void UpdateClioConnectedCompanies(Guid companyID, DateTime lastRefreshDate)
        {

            using (var context = CreateContext())
            {
                var query = from p in context.C_EKPCompanies
                            where p.ID == companyID
                            select p;

                var selectedCompany = query.FirstOrDefault();

                if (selectedCompany != null)
                {
                    selectedCompany.LastRefreshTime = lastRefreshDate;

                    context.SaveChanges();
                }
            }

        }

        /// <summary>Same columns the production EXE writes: FullSyncStart (UTC) with FullSyncStop cleared.</summary>
        public void SetFullSyncStart(Guid companyID)
        {
            using (var context = CreateContext())
            {
                context.Database.ExecuteSqlCommand(
                    "UPDATE dbo._EKPCompanies SET FullSyncStart = @Time, FullSyncStop = NULL WHERE ID = @CompanyId",
                    new SqlParameter("@Time", DateTime.UtcNow),
                    new SqlParameter("@CompanyId", companyID));
            }
        }

        public void SetFullSyncStop(Guid companyID)
        {
            using (var context = CreateContext())
            {
                context.Database.ExecuteSqlCommand(
                    "UPDATE dbo._EKPCompanies SET FullSyncStop = @Time WHERE ID = @CompanyId",
                    new SqlParameter("@Time", DateTime.UtcNow),
                    new SqlParameter("@CompanyId", companyID));
            }
        }

        public void UpdateClioConnectedCompaniesLastSyncTime(Guid companyID, DateTime LastSyncTime)
        {
            using (var context = CreateContext())
            {
                var query = from p in context.C_EKPCompanies
                            where p.ID == companyID
                            select p;

                var selectedCompany = query.FirstOrDefault();

                if (selectedCompany != null)
                {
                    selectedCompany.LastSyncTime = LastSyncTime;

                    context.SaveChanges();
                }
            }
        }

        public void UpdateClioConnectedCompanies(Guid companyID, DateTime lastTokenRefreshDate, string newToken)
        {
            if (!string.IsNullOrWhiteSpace(newToken))
            {
                using (var context = CreateContext())
                {
                    var query = from p in context.C_EKPCompanies
                                where p.ID == companyID
                                select p;

                    var selectedCompany = query.FirstOrDefault();

                    if (selectedCompany != null)
                    {
                        selectedCompany.LastTokenRefreshTime = lastTokenRefreshDate;
                        //selectedCompany.LastSyncTime = lastTokenRefreshDate;
                        selectedCompany.ClioToken = newToken;

                        context.SaveChanges();
                    }
                }
            }
        }

        internal void SaveCalender(List<CalenderItem> data, Guid companyID)
        {
            if (data != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in data select (int?)q.id).ToList();

                        var dataToUpdate = from p in context.Calendars
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.Calendars.Remove(item);
                        }

                        context.SaveChanges();
                    }

                    foreach (var item in data)
                    {
                        context.Calendars.Add(new Calendar()
                        {

                            ID = Convert.ToInt64(item.id),
                            Clio_Internal_ID = item.id,
                            ETag = item.etag,
                            Redacted = 0,
                            JSON = JsonConvert.SerializeObject(item),
                            Created_At = item.created_at.ToString(),
                            Updated_At = item.updated_at.ToString(),
                            CompanyID = companyID,

                            Name = CleanStringRemoveArabic(item.name, false),

                            Color = 0,
                            Court_Rules_Default_Calendar = item.court_rules_default_calendar ? 1 : 0,
                            Creator = item.creator.id,
                            Visible = item.visible ? 1 : 0,
                        });
                    }

                    SaveChangesForTiming(context);
                }
            }
        }

        internal void SaveCalenderEntries(List<CalenderEntry> data, Guid companyID)
        {
            if (data != null)
            {
                using (var context = CreateSaveContext())
                {
                    if (_updateData)
                    {
                        List<int?> filter = (from q in data select (int?)q.id).ToList();

                        var dataToUpdate = from p in context.CalendarEntries
                                           where filter.Contains(p.Clio_Internal_ID) && p.CompanyID == companyID
                                           select p;

                        foreach (var item in dataToUpdate)
                        {
                            context.CalendarEntries.Remove(item);
                        }

                        context.SaveChanges();
                    }

                    foreach (var item in data)
                    {
                        context.CalendarEntries.Add(new CalendarEntry()
                        {

                            ID = Convert.ToInt64(item.id),
                            Clio_Internal_ID = item.id,
                            ETag = item.etag,
                            Redacted = 0,
                            JSON = JsonConvert.SerializeObject(item),
                            Created_At = item.created_at.ToString(),
                            Updated_At = item.updated_at.ToString(),
                            CompanyID = companyID,
                            Summary = item.summary,
                            Location = item.location,
                            Matter = item.matter?.id,
                            Calendar_Owner = item.calendar_owner?.id,

                        });
                    }

                    if (!_updateData)
                    {
                        BulkInsertEntities<CalendarEntry>(context, "CalendarEntries", "BulkBatchSize.CalendarEntries");
                        return;
                    }

                    SaveChangesForTiming(context);
                }
            }
        }

        internal void ConvertViewsToTable()
        {
            Console.WriteLine("SP_CustomExecAfterSync started");
            ExecuteSP("[dbo].[SP_CustomExecAfterSync]");
            Console.WriteLine("SP_CustomExecAfterSync end");

            Console.WriteLine("SP_InsertIntoMattersDetailedCustomFields started");
            ExecuteSP("[dbo].[SP_InsertIntoMattersDetailedCustomFields]");
            Console.WriteLine("SP_InsertIntoMattersDetailedCustomFields end");

            Console.WriteLine("PopulateMatterNotesView started");
            ExecuteSP("[dbo].[PopulateMatterNotesView]");
            Console.WriteLine("PopulateMatterNotesView end");

            Console.WriteLine("SP_UpdateNonBillableActivities started");
            ExecuteSP("[dbo].[SP_UpdateNonBillableActivities]");
            Console.WriteLine("SP_UpdateNonBillableActivities end");

            Console.WriteLine("PopulateTables started");
            ExecuteSP("[dbo].[PopulateTables]");
            Console.WriteLine("PopulateTables end");

            //Console.WriteLine("SP_GetViewsToConvertToTable started");
            //var dataSet = ExecuteSPWithDataSet("[dbo].[SP_GetViewsToConvertToTable]");
            //Console.WriteLine("SP_GetViewsToConvertToTable end");

            //if (dataSet != null && dataSet.Tables != null && dataSet.Tables.Count > 0)
            //{
            //    for (int i = 0; i < dataSet.Tables[0].Rows.Count; i++)
            //    {
            //        var viewName = dataSet.Tables[0].Rows[i]["ViewName"];
            //        Console.WriteLine($"PopulateTables started for view {viewName}");
            //        ExecuteSP("[dbo].[PopulateTables]", new SqlParameter("@specificViewName", SqlDbType.VarChar) { Value = viewName });
            //        Console.WriteLine($"PopulateTables end for view {viewName}");
            //    }
            //}

            Console.WriteLine("UpdateExpectedHoursDaily started");
            ExecuteSP("[dbo].[UpdateExpectedHoursDaily]");
            Console.WriteLine("UpdateExpectedHoursDaily end");

            Console.WriteLine("SP_UpdateNonBillableActivities started");
            ExecuteSP("[dbo].[SP_UpdateNonBillableActivities]");
            Console.WriteLine("SP_UpdateNonBillableActivities end");

            Console.WriteLine("SP_AddUserGroupReportTo started");
            ExecuteSP("[dbo].[SP_AddUserGroupReportTo]");
            Console.WriteLine("SP_AddUserGroupReportTo end");
        }
        private static readonly string[] PreSyncViews =
        {
            "VW_Users",
            "VW_Contacts",
            "VW_Folders",
            "VW_PracticeAreas",
            "VW_Matters"
        };
        internal void SyncMatters()
        {
            Console.WriteLine("Pre-sync view population started");
            PopulateViewsBeforeSync();

            //Console.WriteLine("SP_Sync_Matters_From_View started");
            //ExecuteSP("[dbo].[SP_Sync_Matters_From_View]");
            //Console.WriteLine("SP_Sync_Matters_From_View end");
        }
        private void PopulateViewsBeforeSync()
        {
            foreach (var viewName in PreSyncViews)
            {
                Console.WriteLine($"PopulateTables started for view {viewName}");

                ExecuteSP(
                    "[dbo].[PopulateTables]",
                    new SqlParameter("@specificViewName", SqlDbType.VarChar)
                    {
                        Value = viewName
                    });

                Console.WriteLine($"PopulateTables end for view {viewName}");
            }
        }

        internal void ExecuteSP(string storedProcedureName, params SqlParameter[] parameters)
        {
            using (var context = CreateContextFoSP())
            {
                var sql = $"EXEC {storedProcedureName}";

                if (parameters.Length > 0)
                {
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        sql += (i == 0 ? " " : ", ") + parameters[i].ParameterName + " = @" + parameters[i].ParameterName.TrimStart('@');
                    }

                    context.Database.ExecuteSqlCommand(sql, parameters);
                }
                else
                {
                    context.Database.ExecuteSqlCommand(sql);
                }
            }
        }

        internal DataSet ExecuteSPWithDataSet(string storedProcedureName, params SqlParameter[] parameters)
        {
            var dataSet = new DataSet();

            using (var context = CreateContextFoSP())
            {
                var sql = $"EXEC {storedProcedureName}";

                if (parameters.Length > 0)
                {
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        sql += (i == 0 ? " " : ", ") + parameters[i].ParameterName + " = @" + parameters[i].ParameterName.TrimStart('@');
                    }
                }

                using (var connection = context.Database.Connection)
                {
                    connection.Open();

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = sql;
                        command.CommandType = CommandType.Text;

                        foreach (var param in parameters)
                        {
                            command.Parameters.Add(param);
                        }

                        using (var adapter = new SqlDataAdapter((SqlCommand)command))
                        {
                            adapter.Fill(dataSet);
                        }
                    }
                }
            }

            return dataSet;
        }

        public HashSet<long> GetCompletedDocumentIds()
        {
            using (var context = CreateContext())
            {
                return new HashSet<long>(
             context.Database.SqlQuery<long>(
                 "SELECT DocumentID FROM SyncedDocument WHERE Status = 'Completed'")
             .ToList());
            }
        }

        public int GetRetryCount(long documentId)
        {
            using (var context = CreateContext())
            {
                return context.Database.SqlQuery<int>(
            @"SELECT ISNULL(RetryCount, 0)
              FROM SyncedDocument
              WHERE DocumentID = @p0",
            documentId)
            .FirstOrDefault();
            }
        }

        public List<ClioMatterDocumentTarget> GetActiveMattersForDocumentSync(Guid companyId)
        {
            using (var context = CreateContext())
            {
                return context.Database.SqlQuery<ClioMatterDocumentTarget>(
                    @"
            SELECT
                CAST(Clio_Internal_ID AS BIGINT) AS MatterId,
                    JSON_VALUE(CAST(JSON AS NVARCHAR(MAX)), '$.number') AS MatterNumber,
                Display_Number AS DisplayNumber
            FROM Matters
            WHERE CompanyID = @p0
              AND Clio_Internal_ID IS NOT NULL",
                    companyId)
                    .ToList();
            }
        }

        public Dictionary<long, (string Status, int? ClioVersionNumber)> GetSyncedDocumentVersionMap()
        {
            using (var context = CreateContext())
            {
                return context.Database.SqlQuery<SyncedDocumentVersionRow>(
                    @"
                    SELECT
                        DocumentID,
                        Status,
                        ClioVersionNumber
                    FROM SyncedDocument")
                    .ToList()
                    .ToDictionary(
                        row => row.DocumentID,
                        row => (row.Status, row.ClioVersionNumber));
            }
        }

        public void MarkDocumentUpdated(long documentId)
        {
            using (var context = CreateContext())
            {
                context.Database.ExecuteSqlCommand(
                    @"
                    UPDATE SyncedDocument
                    SET IsUpdated = 1,
                        Status = 'Pending',
                        UpdatedAt = GETDATE()
                    WHERE DocumentID = @p0",
                    documentId);
            }
        }

        public void SetDocumentVersionNumber(long documentId, int clioVersionNumber)
        {
            using (var context = CreateContext())
            {
                context.Database.ExecuteSqlCommand(
                    @"
            UPDATE SyncedDocument
            SET ClioVersionNumber = @p1,
                UpdatedAt = GETDATE()
            WHERE DocumentID = @p0
              AND ClioVersionNumber IS NULL", // extra safety: only touches rows still unset
                    documentId, clioVersionNumber);
            }
        }

        public SyncedDocument GetSyncedDocumentById(long documentId)
        {
            using (var context = CreateContext())
            {
                return context.Database.SqlQuery<SyncedDocument>(
                    @"
                    SELECT
                        ID,
                        DocumentID,
                        MatterID,
                        MatterNumber,
                        DocumentName,
                        FilePath,
                        Status,
                        RetryCount,
                        ErrorMessage,
                        DownloadedAt,
                        CreatedAt,
                        UpdatedAt,
                        PushedToApiAt,
                        PushApiStatus
                    FROM SyncedDocument
                    WHERE DocumentID = @p0",
                    documentId)
                    .FirstOrDefault();
            }
        }

        public void MarkDocumentDeleted(long documentId, bool deleteFileFromDisk, Action<Exception, string> logError)
        {
            var syncedDocument = GetSyncedDocumentById(documentId);

            using (var context = CreateContext())
            {
                context.Database.ExecuteSqlCommand(
                    @"
                    UPDATE SyncedDocument
                    SET Status = 'Deleted',
                        DeletedAt = GETDATE(),
                        UpdatedAt = GETDATE()
                    WHERE DocumentID = @p0",
                    documentId);
            }

            if (!deleteFileFromDisk || syncedDocument == null)
            {
                return;
            }

            try
            {
                var filePath = Path.Combine(syncedDocument.FilePath ?? string.Empty, syncedDocument.DocumentName ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                logError?.Invoke(ex, $"Documents | Delete local file | DocumentId: {documentId}");
            }
        }

        public void UpsertSyncedDocument(
            long documentId, long matterId, string matterNumber, string documentName,
            string filePath, string status, string errorMessage = null, bool incrementRetry = false,
            int? clioVersionNumber = null, DateTime? lastUpdatedDate = null)
        {
            // Callers vary in whether they pass a matter number straight from the JSON-derived
            // VW_Matters view (e.g. "30005.0") or an already-cleaned value. Normalize once here so
            // every write path (Pending insert, Completed update, retry/failure updates) ends up with
            // the same plain-integer format instead of the "Completed" row silently reverting to
            // "30005.0" and breaking downstream joins on MatterNumber (e.g. the MatterSyncJob SP).
            matterNumber = NormalizeMatterNumber(matterNumber);

            using (var context = CreateContext())
            {
                context.Database.ExecuteSqlCommand(
                @"
        IF EXISTS (SELECT 1 FROM SyncedDocument WHERE DocumentID = @p0)
        BEGIN
            UPDATE SyncedDocument
            SET
                MatterID = @p1,
                MatterNumber = @p2,
                DocumentName = @p3,
                FilePath = @p4,
                Status = @p5,
                ErrorMessage = @p6,
                ClioVersionNumber = CASE
                                        WHEN @p8 IS NULL THEN ClioVersionNumber
                                        ELSE @p8
                                    END,
                IsUpdated = CASE
                                WHEN @p5 = 'Completed' THEN 0
                                ELSE IsUpdated
                            END,
                DeletedAt = CASE
                                WHEN @p5 = 'Deleted' THEN ISNULL(DeletedAt, GETDATE())
                                ELSE DeletedAt
                            END,
                RetryCount = CASE
                                WHEN @p7 = 1 THEN RetryCount + 1
                                ELSE RetryCount
                             END,
                DownloadedAt = CASE
                                  WHEN @p5 = 'Completed'
                                  THEN GETDATE()
                                  ELSE DownloadedAt
                               END,
LastUpdatedDate = CASE
                     WHEN @p9 IS NULL THEN LastUpdatedDate
                     ELSE @p9
                  END,
                UpdatedAt = GETDATE()
            WHERE DocumentID = @p0
        END
        ELSE
        BEGIN
            INSERT INTO SyncedDocument
            (
                DocumentID,
                MatterID,
                MatterNumber,
                DocumentName,
                FilePath,
                Status,
                RetryCount,
                ErrorMessage,
                DownloadedAt,
                CreatedAt,
                UpdatedAt,
                IsUpdated,
                ClioVersionNumber,
                DeletedAt,
                LastUpdatedDate
            )
            VALUES
            (
                @p0,
                @p1,
                @p2,
                @p3,
                @p4,
                @p5,
                CASE WHEN @p7 = 1 THEN 1 ELSE 0 END,
                @p6,
                CASE WHEN @p5 = 'Completed' THEN GETDATE() ELSE NULL END,
                GETDATE(),
                GETDATE(),
                CASE WHEN @p5 = 'Completed' THEN 0 ELSE 0 END,
                @p8,
                CASE WHEN @p5 = 'Deleted' THEN GETDATE() ELSE NULL END,
                @p9
            )
        END",
                documentId,
                matterId,
                matterNumber.ToString() ?? string.Empty,
                documentName ?? string.Empty,
                filePath ?? string.Empty,
                status,
                errorMessage ?? string.Empty,
                incrementRetry ? 1 : 0,
                clioVersionNumber.HasValue ? (object)clioVersionNumber.Value : DBNull.Value,
                lastUpdatedDate.HasValue ? (object)lastUpdatedDate.Value : DBNull.Value);
            }
        }
        public DocumentSyncInfo GetDocumentSyncInfo(List<ClioMatterDocumentTarget> matterTargets)
        {
            if (matterTargets == null || matterTargets.Count == 0)
                return new DocumentSyncInfo();

            var matterIds = string.Join(",", matterTargets.Select(x => x.MatterId));

            using (var context = CreateContext())
            {
                var sql = $@"
SELECT
    COUNT(CASE WHEN LastUpdatedDate IS NULL THEN 1 END) AS NullCount,
    MAX(LastUpdatedDate) AS LastUpdatedDate
FROM SyncedDocument
WHERE MatterID IN ({matterIds})";

                return context.Database.SqlQuery<DocumentSyncInfo>(sql).FirstOrDefault();
            }
        }
        /// <summary>
        /// Strips a trailing ".0"-style decimal suffix from matter numbers sourced from
        /// JSON_VALUE(...) (e.g. "30005.0" -> "30005"). Non-numeric or already-clean values pass
        /// through unchanged.
        /// </summary>
        private static string NormalizeMatterNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return decimal.TryParse(value.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var number)
                ? number.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                : value.Trim();
        }

        private sealed class SyncedDocumentVersionRow
        {
            public long DocumentID { get; set; }
            public string Status { get; set; }
            public int? ClioVersionNumber { get; set; }
        }

    }
}
