using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CSP.Clio.Sync.Data
{
    public partial class EKPBIEntities : DbContext
    {




        public void SaveChangesInBatches(int batchSize = 50)
        {
            var changeSet = this.ChangeTracker.Entries().Where(e => e.State == EntityState.Added || e.State == EntityState.Modified || e.State == EntityState.Deleted).ToList();
            int count = 0;

            using (var transaction = this.Database.BeginTransaction())
            {
                try
                {
                    foreach (var entity in changeSet)
                    {
                        count++;
                        this.SaveChanges();

                        if (count % batchSize == 0)
                        {
                            // Commit every 'batchSize' operations and begin a new transaction
                            transaction.Commit();
                            transaction.Dispose();
                            this.Database.BeginTransaction();
                        }
                    }

                    // Ensure any remaining changes are committed
                    if (count % batchSize != 0)
                    {
                        transaction.Commit();
                    }
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }


    }
}
