using System;

using PX.CCProcessingBase.Interfaces.V2;
using PX.Common;
using PX.Data;
using PX.Objects.AR.CCPaymentProcessing.Repositories;
using PX.Objects.CC;
using PX.Objects.CC.PaymentProcessing.Helpers;
using PX.Objects.Extensions.PayLink;
using PX.Objects.FS;

namespace PaymentLinksForServiceOrder
{
    public class PayLinkProcessingExt : PX.Objects.CC.PaymentProcessing.PayLinkProcessing
    {
        protected PXGraph Graph;
        public PayLinkProcessingExt(ICCPaymentProcessingRepository repo) : base(repo)
        {
            Graph = repo.Graph;
        }
        #region copypaste
        private CCPayLink UpdatePayLink(CCPayLink payLink)
        {
            return Graph.Caches[typeof(CCPayLink)].Update(payLink) as CCPayLink;
        }
        private CCPayLink InsertPayLink(CCPayLink payLink)
        {
            return Graph.Caches[typeof(CCPayLink)].Insert(payLink) as CCPayLink;
        }
        private CCPayLink GetPayLinkById(int? payLinkId)
        {
            return CCPayLink.PK.Find(Graph, payLinkId);
        }
        private void CheckPayLinkNotProcessed(CCPayLink payLink, PayLinkProcessingParams payLinkData)
        {
            if (!PayLinkHelper.PayLinkWasProcessed(payLink)
                && PayLinkHelper.PayLinkCreated(payLink))
            {
                var docType = payLinkData.DocumentData.DocType;
                var refNbr = payLinkData.DocumentData.DocRefNbr;
                throw new PXException(PX.Objects.CC.Messages.DocumentHasActivePayLink, docType, refNbr);
            }
        }
        private void CheckTimeOfAttemptToCreateLink(CCPayLink payLink)
        {
            //Sometimes BE and a user through UI try to create a link almost at the same time.
            var res = DateTime.UtcNow.Subtract(payLink.StatusDate.Value).TotalMilliseconds;
            if (res <= 5000)
            {
                throw new PXException(ErrorMessages.PrevOperationNotCompleteYet);
            }
        }
        private void SetStatusDate(CCPayLink payLink)
        {
            payLink.StatusDate = PXTimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, LocaleInfo.GetTimeZone());
        }
        private void Save()
        {
            var action = Graph.Actions["Save"];
            if (action != null)
            {
                action.Press();
            }
            else
            {
                Graph.Actions.PressSave();
            }
        }
        #endregion

        public CCPayLink CreateLinkInDBFS(PayLinkDocument doc, PayLinkProcessingParams payLinkData)
        {
            CCPayLink oldPayLink = null;
            if (doc.PayLinkID != null)
            {
                oldPayLink = GetPayLinkById(doc.PayLinkID);
            }

            if (oldPayLink != null)
            {
                CheckPayLinkNotProcessed(oldPayLink, payLinkData);
            }

            CCPayLink payLink;
            if (oldPayLink != null && !PayLinkHelper.PayLinkCreated(oldPayLink))
            {
                CheckTimeOfAttemptToCreateLink(oldPayLink);
                payLink = oldPayLink;
            }
            else
            {
                payLink = new CCPayLink();
                payLink = InsertPayLink(payLink);
            }

            if (!PayLinkHelper.PayLinkCreated(payLink) && payLink.NoteID != null
                && payLink.ActionStatus == PayLinkActionStatus.Error)
            {
                payLinkData.CheckLinkByGuid = true;
            }

            payLink.Action = PayLinkAction.Insert;
            payLink.Amount = payLinkData.Amount;
            payLink.DeliveryMethod = doc.DeliveryMethod;
            payLink.ProcessingCenterID = doc.ProcessingCenterID;
            payLink.CuryID = doc.CuryID;
            payLink.DueDate = payLinkData.DueDate;

            var paylinkExt = PXCache<CCPayLink>.GetExtension<CCPayLink_Ext>(payLink);

            paylinkExt.UsrFSOrderType = ((FSServiceOrder)doc.Base).SrvOrdType;
            paylinkExt.UsrFSOrderNbr = ((FSServiceOrder)doc.Base).RefNbr;


            payLink.NeedSync = false;
            payLink.ActionStatus = PayLinkActionStatus.Open;
            payLink.LinkStatus = PX.Objects.CC.PayLinkStatus.None;
            payLink.PaymentStatus = PX.Objects.CC.PayLinkPaymentStatus.None;
            payLink.ErrorMessage = null;
            SetStatusDate(payLink);
            payLink = UpdatePayLink(payLink);
            Save();
            payLinkData.LinkGuid = payLink.NoteID;
            doc.PayLinkID = payLink.PayLinkID;
            return payLink;
        }
    }
}
