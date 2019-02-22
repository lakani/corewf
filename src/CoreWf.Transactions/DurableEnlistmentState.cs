// This file is part of Core WF which is licensed under the MIT license.
// See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.Threading;

namespace System.Activities.Transactions
{
    // Base class for all durable enlistment states
    internal abstract class DurableEnlistmentState : EnlistmentState
    {
        // Double-checked locking pattern requires volatile for read/write synchronization
        private static volatile DurableEnlistmentActive s_durableEnlistmentActive;
        private static volatile DurableEnlistmentAborting s_durableEnlistmentAborting;
        private static volatile DurableEnlistmentCommitting s_durableEnlistmentCommitting;
        private static volatile DurableEnlistmentDelegated s_durableEnlistmentDelegated;
        private static volatile DurableEnlistmentEnded s_durableEnlistmentEnded;

        // Object for synchronizing access to the entire class( avoiding lock( typeof( ... )) )
        private static object s_classSyncObject;

        internal static DurableEnlistmentActive DurableEnlistmentActive
        {
            get
            {
                if (s_durableEnlistmentActive == null)
                {
                    lock (ClassSyncObject)
                    {
                        if (s_durableEnlistmentActive == null)
                        {
                            DurableEnlistmentActive temp = new DurableEnlistmentActive();
                            s_durableEnlistmentActive = temp;
                        }
                    }
                }

                return s_durableEnlistmentActive;
            }
        }

        protected static DurableEnlistmentAborting DurableEnlistmentAborting
        {
            get
            {
                if (s_durableEnlistmentAborting == null)
                {
                    lock (ClassSyncObject)
                    {
                        if (s_durableEnlistmentAborting == null)
                        {
                            DurableEnlistmentAborting temp = new DurableEnlistmentAborting();
                            s_durableEnlistmentAborting = temp;
                        }
                    }
                }

                return s_durableEnlistmentAborting;
            }
        }

        protected static DurableEnlistmentCommitting DurableEnlistmentCommitting
        {
            get
            {
                if (s_durableEnlistmentCommitting == null)
                {
                    lock (ClassSyncObject)
                    {
                        if (s_durableEnlistmentCommitting == null)
                        {
                            DurableEnlistmentCommitting temp = new DurableEnlistmentCommitting();
                            s_durableEnlistmentCommitting = temp;
                        }
                    }
                }

                return s_durableEnlistmentCommitting;
            }
        }

        protected static DurableEnlistmentDelegated DurableEnlistmentDelegated
        {
            get
            {
                if (s_durableEnlistmentDelegated == null)
                {
                    lock (ClassSyncObject)
                    {
                        if (s_durableEnlistmentDelegated == null)
                        {
                            DurableEnlistmentDelegated temp = new DurableEnlistmentDelegated();
                            s_durableEnlistmentDelegated = temp;
                        }
                    }
                }

                return s_durableEnlistmentDelegated;
            }
        }

        protected static DurableEnlistmentEnded DurableEnlistmentEnded
        {
            get
            {
                if (s_durableEnlistmentEnded == null)
                {
                    lock (ClassSyncObject)
                    {
                        if (s_durableEnlistmentEnded == null)
                        {
                            DurableEnlistmentEnded temp = new DurableEnlistmentEnded();
                            s_durableEnlistmentEnded = temp;
                        }
                    }
                }

                return s_durableEnlistmentEnded;
            }
        }

        // Helper object for static synchronization
        private static object ClassSyncObject
        {
            get
            {
                if (s_classSyncObject == null)
                {
                    object o = new object();
                    Interlocked.CompareExchange(ref s_classSyncObject, o, null);
                }
                return s_classSyncObject;
            }
        }
    }

    // Active state for a durable enlistment.  In this state the transaction can be aborted 
    // asynchronously by calling abort.
    internal class DurableEnlistmentActive : DurableEnlistmentState
    {
        internal override void EnterState(InternalEnlistment enlistment)
        {
            // Set the enlistment state
            enlistment.State = this;

            // Yeah it's active
        }

        internal override void EnlistmentDone(InternalEnlistment enlistment)
        {
            // Mark the enlistment as done.
            DurableEnlistmentEnded.EnterState(enlistment);
        }

        internal override void InternalAborted(InternalEnlistment enlistment)
        {
            // Transition to the aborting state
            DurableEnlistmentAborting.EnterState(enlistment);
        }

        internal override void ChangeStateCommitting(InternalEnlistment enlistment)
        {
            // Transition to the committing state
            DurableEnlistmentCommitting.EnterState(enlistment);
        }

        internal override void ChangeStatePromoted(InternalEnlistment enlistment, IPromotedEnlistment promotedEnlistment)
        {
            // Save the promoted enlistment because future notifications must be sent here.
            enlistment.PromotedEnlistment = promotedEnlistment;

            // The transaction is being promoted promote the enlistment as well
            EnlistmentStatePromoted.EnterState(enlistment);
        }

        internal override void ChangeStateDelegated(InternalEnlistment enlistment)
        {
            // This is a valid state transition.
            DurableEnlistmentDelegated.EnterState(enlistment);
        }
    }

    // Aborting state for a durable enlistment.  In this state the transaction has been aborted,
    // by someone other than the enlistment.
    //
    internal class DurableEnlistmentAborting : DurableEnlistmentState
    {
        internal override void EnterState(InternalEnlistment enlistment)
        {
            // Set the enlistment state
            enlistment.State = this;

            Monitor.Exit(enlistment.Transaction);
            try
            {
                TransactionsEtwProvider etwLog = TransactionsEtwProvider.Log;
                if (etwLog.IsEnabled())
                {
                    etwLog.EnlistmentStatus(enlistment, NotificationCall.Rollback);
                }

                // Send the Rollback notification to the enlistment
                if (enlistment.SinglePhaseNotification != null)
                {
                    enlistment.SinglePhaseNotification.Rollback(enlistment.SinglePhaseEnlistment);
                }
                else
                {
                    enlistment.PromotableSinglePhaseNotification.Rollback(enlistment.SinglePhaseEnlistment);
                }
            }
            finally
            {
                Monitor.Enter(enlistment.Transaction);
            }
        }

        internal override void Aborted(InternalEnlistment enlistment, Exception e)
        {
            if (enlistment.Transaction._innerException == null)
            {
                enlistment.Transaction._innerException = e;
            }

            // Transition to the ended state
            DurableEnlistmentEnded.EnterState(enlistment);
        }

        internal override void EnlistmentDone(InternalEnlistment enlistment)
        {
            // Transition to the ended state
            DurableEnlistmentEnded.EnterState(enlistment);
        }
    }

    // Committing state is when SPC has been sent to an enlistment but no response
    // has been received.
    //
    internal class DurableEnlistmentCommitting : DurableEnlistmentState
    {
        internal override void EnterState(InternalEnlistment enlistment)
        {
            bool spcCommitted = false;
            // Set the enlistment state
            enlistment.State = this;

            Monitor.Exit(enlistment.Transaction);
            try
            {
                TransactionsEtwProvider etwLog = TransactionsEtwProvider.Log;
                if (etwLog.IsEnabled())
                {
                    etwLog.EnlistmentStatus(enlistment, NotificationCall.SinglePhaseCommit);
                }

                // Send the Commit notification to the enlistment
                if (enlistment.SinglePhaseNotification != null)
                {
                    enlistment.SinglePhaseNotification.SinglePhaseCommit(enlistment.SinglePhaseEnlistment);
                }
                else
                {
                    enlistment.PromotableSinglePhaseNotification.SinglePhaseCommit(enlistment.SinglePhaseEnlistment);
                }
                spcCommitted = true;
            }
            finally
            {
                if (!spcCommitted)
                {
                    enlistment.SinglePhaseEnlistment.InDoubt();
                }
                Monitor.Enter(enlistment.Transaction);
            }
        }

        internal override void EnlistmentDone(InternalEnlistment enlistment)
        {
            // EnlistmentDone should be treated the same as Committed from this state.
            // This eliminates a race between the SPC call and the EnlistmentDone call.

            // Transition to the ended state
            DurableEnlistmentEnded.EnterState(enlistment);

            // Make the transaction commit
            enlistment.Transaction.State.ChangeStateTransactionCommitted(enlistment.Transaction);
        }

        internal override void Committed(InternalEnlistment enlistment)
        {
            // Transition to the ended state
            DurableEnlistmentEnded.EnterState(enlistment);

            // Make the transaction commit
            enlistment.Transaction.State.ChangeStateTransactionCommitted(enlistment.Transaction);
        }

        internal override void Aborted(InternalEnlistment enlistment, Exception e)
        {
            // Transition to the ended state
            DurableEnlistmentEnded.EnterState(enlistment);

            // Start the transaction aborting
            enlistment.Transaction.State.ChangeStateTransactionAborted(enlistment.Transaction, e);
        }

        internal override void InDoubt(InternalEnlistment enlistment, Exception e)
        {
            // Transition to the ended state
            DurableEnlistmentEnded.EnterState(enlistment);

            if (enlistment.Transaction._innerException == null)
            {
                enlistment.Transaction._innerException = e;
            }

            // Make the transaction in dobut
            enlistment.Transaction.State.InDoubtFromEnlistment(enlistment.Transaction);
        }
    }

    // Delegated state for a durable enlistment represents an enlistment that was
    // origionally a PromotableSinglePhaseEnlisment that where promotion has happened.
    // These enlistments don't need to participate in the commit process anymore.
    internal class DurableEnlistmentDelegated : DurableEnlistmentState
    {
        internal override void EnterState(InternalEnlistment enlistment)
        {
            // Set the enlistment state
            enlistment.State = this;

            // At this point the durable enlistment should have someone to forward to.
            Debug.Assert(enlistment.PromotableSinglePhaseNotification != null);
        }

        internal override void Committed(InternalEnlistment enlistment)
        {
            // Transition to the ended state
            DurableEnlistmentEnded.EnterState(enlistment);

            // Change the transaction to committed.
            enlistment.Transaction.State.ChangeStatePromotedCommitted(enlistment.Transaction);
        }

        internal override void Aborted(InternalEnlistment enlistment, Exception e)
        {
            // Transition to the ended state
            DurableEnlistmentEnded.EnterState(enlistment);

            if (enlistment.Transaction._innerException == null)
            {
                enlistment.Transaction._innerException = e;
            }

            // Start the transaction aborting
            enlistment.Transaction.State.ChangeStatePromotedAborted(enlistment.Transaction);
        }

        internal override void InDoubt(InternalEnlistment enlistment, Exception e)
        {
            // Transition to the ended state
            DurableEnlistmentEnded.EnterState(enlistment);

            if (enlistment.Transaction._innerException == null)
            {
                enlistment.Transaction._innerException = e;
            }

            // Tell the transaction that the enlistment is InDoubt.  Note that
            // for a transaction that has been delegated and then promoted there
            // are two chances to get a better answer than indoubt.  So it may be that
            // the TM will have a better answer.
            enlistment.Transaction.State.InDoubtFromEnlistment(enlistment.Transaction);
        }
    }

    // Ended state is the state that is entered when the durable enlistment has committed,
    // aborted, or said read only for an enlistment.  At this point there are no valid
    // operations on the enlistment.
    internal class DurableEnlistmentEnded : DurableEnlistmentState
    {
        internal override void EnterState(InternalEnlistment enlistment)
        {
            // Set the enlistment state
            enlistment.State = this;
        }

        internal override void InternalAborted(InternalEnlistment enlistment)
        {
            // From the Aborting state the transaction may tell the enlistment to abort.  At this point 
            // it already knows.  Eat this message.
        }

        internal override void InDoubt(InternalEnlistment enlistment, Exception e)
        {
            // Ignore this in case the enlistment gets here before
            // the transaction tells it to do so
        }
    }
}
