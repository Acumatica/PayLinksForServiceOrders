using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using PX.CCProcessingBase.Interfaces.V2;
using PX.Common;
using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using PX.Objects.AR;
using PX.Objects.AR.CCPaymentProcessing;
using PX.Objects.AR.CCPaymentProcessing.Helpers;
using PX.Objects.AR.CCPaymentProcessing.Repositories;
using PX.Objects.CA;
using PX.Objects.CC;
using PX.Objects.CC.PaymentProcessing.Helpers;
using PX.Objects.CS;
using PX.Objects.Extensions.PayLink;
using PX.Objects.Extensions.PaymentTransaction;
using PX.Objects.FS;


namespace PaymentLinksForServiceOrder
{
    public class ServiceOrderEntryPayLink : PayLinkDocumentGraph<ServiceOrderEntry, FSServiceOrder>
	{
		public static bool IsActive()
		{
			return PXAccess.FeatureInstalled<FeaturesSet.acumaticaPayments>();
		}
       
        public PXSelect<CCPayLink, Where<CCPayLink.payLinkID, Equal<Current<ServiceOrderPayLink.usrPayLinkID>>>> PayLink;

		[PXUIField(DisplayName = "Create Payment Link", Visible = true)]
		[PXButton(CommitChanges = true)]
		public override IEnumerable CreateLink(PXAdapter adapter)
		{
			SaveDoc();
			var docs = adapter.Get<FSServiceOrder>().ToList();

			PXLongOperation.StartOperation(Base, delegate
			{
				foreach (var doc in docs)
				{
					var graph = PXGraph.CreateInstance<ServiceOrderEntry>();
					var ext = graph.GetExtension<ServiceOrderEntryPayLink>();
					graph.ServiceOrderRecords.Cache.Clear();
					FSServiceOrder invoice = graph.ServiceOrderRecords.Search<FSServiceOrder.refNbr>(doc.RefNbr, doc.SrvOrdType);

					graph.ServiceOrderRecords.Current = invoice;
					ext.CollectDataAndCreateLink();
				}
			});
			return docs;
		}

        protected override bool CheckPayLinkRelatedToDoc(CCPayLink payLink)
        {
            bool flag = true;
            PayLinkDocument current = PayLinkDocument.Current;
            if (current != null && current.PayLinkID.HasValue)
            {
                if (PayLinkDocument.Cache.GetValueOriginal<PayLinkDocument.payLinkID>(current) as int? == current.PayLinkID)
                {
                    return flag;
                }

                PXEntryStatus status = PayLinkDocument.Cache.GetStatus(current);
                if (status == PXEntryStatus.Inserted)
                {
                    flag = false;
                }

              var payLinkExt = PXCache<CCPayLink>.GetExtension<CCPayLink_Ext>(payLink);
                if (status == PXEntryStatus.Updated)
                {
                    if (current.DocType != payLinkExt.UsrFSOrderType || current.RefNbr != payLinkExt.UsrFSOrderNbr)
                    {
                        flag = false;
                    }
                }

                if (!flag)
                {
                    PayLinkDocument.Cache.SetValue<PayLinkDocument.payLinkID>(current, null);
                }
            }

            return flag;
        }

        [PXOverride]
		public virtual void Persist(Action baseMethod)
		{
			var curDoc = Base.ServiceOrderRecords.Current;
			if (curDoc != null)
			{
				var payLink = PayLink.SelectSingle();

				if (payLink != null && CheckPayLinkRelatedToDoc(payLink)
					&& payLink.NeedSync == false && PayLinkHelper.PayLinkOpen(payLink))
				{
					var needSync = payLink.Amount != curDoc.CuryEstimatedOrderTotal
						|| payLink.DueDate != curDoc.OrderDate.Value;

					var origCuryDocBal = Base.ServiceOrderRecords.Cache
						.GetValueOriginal<FSServiceOrder.curyEstimatedOrderTotal>(curDoc) as decimal?;
					var origDueDate = Base.ServiceOrderRecords.Cache
						.GetValueOriginal<FSServiceOrder.orderDate>(curDoc) as DateTime?;

					needSync = needSync && (curDoc.CuryEstimatedOrderTotal != origCuryDocBal
						|| curDoc.OrderDate != origDueDate);

					if (needSync)
					{
						payLink.NeedSync = needSync;
						PayLink.Update(payLink);
					}
				}
			}
			baseMethod();
		}

		public virtual void UpdatePayLinkAndCreatePayments(PayLinkData payLinkData)
		{
			var link = PayLink.SelectSingle();
			var doc = Base.ServiceOrderRecords.Current;
			var payLinkProcessing = GetPayLinkProcessing();
			var copyDoc = Base.ServiceOrderRecords.Cache.CreateCopy(doc) as FSServiceOrder;
			payLinkProcessing.UpdatePayLinkByData(link, payLinkData);
			if (payLinkData.Transactions != null && payLinkData.Transactions.Any())
			{
				try
				{

					CreatePayments(copyDoc, link, payLinkData);
				}
				catch (Exception ex)
				{
					payLinkProcessing.SetErrorStatus(ex, link);
					throw;
				}
			}
			payLinkProcessing.SetLinkStatus(link, payLinkData);
		}

		public override void CollectDataAndSyncLink()
		{
			var payLinkProcessing = GetPayLinkProcessing();
			var doc = Base.ServiceOrderRecords.Current;
			var copyDoc = Base.ServiceOrderRecords.Cache.CreateCopy(doc) as FSServiceOrder;
			var payLinkDoc = PayLinkDocument.Current;
			var link = PayLink.SelectSingle();
			var payLinkData = payLinkProcessing.GetPayments(payLinkDoc, link);
			if (payLinkData.Transactions != null && payLinkData.Transactions.Any())
			{
				try
				{
					CreatePayments(copyDoc, link, payLinkData);
				}
				catch (Exception ex)
				{
					payLinkProcessing.SetErrorStatus(ex, link);
					throw;
				}
			}

			payLinkProcessing.SetLinkStatus(link, payLinkData);

			copyDoc = PXSelectReadonly<FSServiceOrder, Where<FSServiceOrder.srvOrdType, Equal<Required<FSServiceOrder.srvOrdType>>,
                And<FSServiceOrder.refNbr, Equal<Required<FSServiceOrder.refNbr>>>>>
                .Select(Base, copyDoc.SrvOrdType, copyDoc.RefNbr);

			if ((copyDoc.OpenDoc == false || copyDoc.CuryEstimatedOrderTotal == 0)
				&& payLinkData.StatusCode == PX.CCProcessingBase.Interfaces.V2.PayLinkStatus.Open)
			{
				var closeParams = new PayLinkProcessingParams();
				closeParams.LinkGuid = link.NoteID;
				closeParams.ExternalId = link.ExternalID;
				payLinkProcessing.CloseLink(payLinkDoc, link, closeParams);
				return;
			}

			if (link.NeedSync == false || payLinkData.StatusCode == PX.CCProcessingBase.Interfaces.V2.PayLinkStatus.Closed
				 || payLinkData.PaymentStatusCode == PX.CCProcessingBase.Interfaces.V2.PayLinkPaymentStatus.Paid)
			{
				return;
			}

			var data = CollectDataToSyncLink(copyDoc, link);
			payLinkProcessing.SyncLink(payLinkDoc, link, data);
		}

		protected override PX.Objects.CC.PaymentProcessing.PayLinkProcessing GetPayLinkProcessing()
        {
            ICCPaymentProcessingRepository paymentProcessingRepo = GetPaymentProcessingRepo();
            return new PayLinkProcessingExt(paymentProcessingRepo);
        }
        public override void CollectDataAndCreateLink()
		{
			var doc = Base.ServiceOrderRecords.Current;

            var data = CollectDataToCreateLink(doc);
			PayLinkProcessingExt payLinkProcessing = (PayLinkProcessingExt)GetPayLinkProcessing();
			var payLinkDoc = PayLinkDocument.Current;

			CCPayLink payLink;
			using (var scope = new PXTransactionScope())
			{
				payLink = payLinkProcessing.CreateLinkInDBFS(payLinkDoc, data);
				PayLinkDocument.Update(payLinkDoc);

                var docExt = Base.ServiceOrderRecords.Cache.GetExtension<ServiceOrderPayLink>(Base.ServiceOrderRecords.Current);
                Base.Save.Press();
				scope.Complete();
			}

			payLinkProcessing.SendCreateLinkRequest(payLink, data);

			if (payLinkDoc.DeliveryMethod == PayLinkDeliveryMethod.Email)
			{
				SendNotification();
			}
		}

		public void CreateStandalonePayments(FSServiceOrder invoice, CCPayLink payLink, PayLinkData payLinkData)
		{
			CreatePayments(invoice, payLink, payLinkData, true);
		}

		public virtual void CreatePayments(FSServiceOrder invoice, CCPayLink payLink, PayLinkData payLinkData)
		{
			CreatePayments(invoice, payLink, payLinkData, false);
		}

		public override void SendNotification()
		{
			const string id = "INVOICE PAY LINK";

			var payLinkDoc = PayLinkDocument.Current;
			if (payLinkDoc.DeliveryMethod != PayLinkDeliveryMethod.Email) return;

			var invoice = Base.ServiceOrderRecords.Current;
			//var prms = new Dictionary<string, string>
			//{
			//	["DocType"] = invoice.DocType,
			//	["RefNbr"] = invoice.RefNbr
			//};
			//var activityExt = Base.GetExtension<ARInvoiceEntry_ActivityDetailsExt>();
			//activityExt.SendNotification(ARNotificationSource.Customer, id, invoice.BranchID, prms, true);
		}

		protected virtual void _(Events.RowSelected<FSServiceOrder> e)
		{
			var doc = e.Row;
			var cache = e.Cache;
			if (doc == null) return;

			var disablePayLink = false;
			var allowOverrideDeliveryMethod = false;
			var custClass = GetCustomerClass();
			if (custClass != null)
			{
				var custClassExt = GetCustomerClassExt(custClass);
				disablePayLink = custClassExt.DisablePayLink.GetValueOrDefault();
				allowOverrideDeliveryMethod = custClassExt.AllowOverrideDeliveryMethod.GetValueOrDefault();
			}

			var docExt = PayLinkDocument.Current;
			var link = PayLink.SelectSingle();
			var needNewLink = link?.Url == null || PayLinkHelper.PayLinkWasProcessed(link);
			var closedAndUnpaid = link != null && PayLinkHelper.PayLinkWasProcessed(link)
				&& link.PaymentStatus == PX.Objects.CC.PayLinkPaymentStatus.Unpaid;

			createLink.SetEnabled(doc.OpenDoc == true
				&& docExt.ProcessingCenterID != null && needNewLink && !disablePayLink);
			createLink.SetVisible(!disablePayLink);
			syncLink.SetEnabled(!needNewLink
				&& !disablePayLink && docExt.ProcessingCenterID != null);
			syncLink.SetVisible(!disablePayLink);
			resendLink.SetEnabled(!needNewLink
				&& docExt.ProcessingCenterID != null && !disablePayLink && docExt.DeliveryMethod == PayLinkDeliveryMethod.Email);
			resendLink.SetVisible(!disablePayLink);

			var hidePayLinkFields = disablePayLink && PayLink.SelectSingle()?.Url == null;
			var payLinkControlsVisible = !hidePayLinkFields;
			var payLinkControlsEnabled = payLinkControlsVisible && (link?.Url == null || closedAndUnpaid);

			PXUIFieldAttribute.SetEnabled<ServiceOrderPayLink.usrProcessingCenterID>(cache, doc, payLinkControlsEnabled);
			PXUIFieldAttribute.SetEnabled<ServiceOrderPayLink.usrDeliveryMethod>(cache, doc, payLinkControlsEnabled
				&& allowOverrideDeliveryMethod);

			PXUIFieldAttribute.SetVisible<ServiceOrderPayLink.usrProcessingCenterID>(cache, doc, payLinkControlsVisible);
			PXUIFieldAttribute.SetVisible<ServiceOrderPayLink.usrDeliveryMethod>(cache, doc, payLinkControlsVisible);
			PXUIFieldAttribute.SetVisible<CCPayLink.url>(PayLink.Cache, null, payLinkControlsVisible);
			PXUIFieldAttribute.SetVisible<CCPayLink.linkStatus>(PayLink.Cache, null, payLinkControlsVisible);
		}

		protected virtual void _(Events.RowSelected<CCPayLink> e)
		{
			var doc = e.Row;
			var cache = e.Cache;
			if (doc == null) return;

			ShowActionStatusWarningIfNeeded(cache, doc);
		}

		protected virtual void _(Events.FieldDefaulting<ServiceOrderPayLink.usrDeliveryMethod> e)
		{
			var row = e.Row as FSServiceOrder;

			var custClass = GetCustomerClass();
			if (custClass == null) return;

			var custClassExt = GetCustomerClassExt(custClass);
			if (custClassExt.DisablePayLink.GetValueOrDefault() == false)
			{
				e.NewValue = custClassExt.DeliveryMethod;
			}
		}

		protected virtual void _(Events.FieldUpdated<FSServiceOrder.curyID> e)
		{
			var order = (FSServiceOrder)e.Row;
			var cache = e.Cache;
			if (order == null) return;

			cache.SetDefaultExt<ServiceOrderPayLink.usrProcessingCenterID>(order);
		}

		protected virtual void _(Events.FieldUpdated<FSServiceOrder, FSServiceOrder.branchID> e)
		{
			var invoice = e.Row;
			var cache = e.Cache;
			var newVal = e.NewValue as int?;
			var oldVal = e.OldValue as int?;
			if (invoice == null) return;
			if (newVal == oldVal) return;

			var payLink = PayLink.SelectSingle();
			if (payLink?.Url != null && !PayLinkHelper.PayLinkWasProcessed(payLink)) return;

			cache.SetDefaultExt<ServiceOrderPayLink.usrProcessingCenterID>(invoice);
		}

		protected virtual void _(Events.FieldUpdated<FSServiceOrder.customerID> e)
		{
			var invoice = (FSServiceOrder)e.Row;
			var cache = e.Cache;
			if (invoice != null)
			{
				cache.SetDefaultExt<ServiceOrderPayLink.usrDeliveryMethod>(invoice);
				cache.SetDefaultExt<ServiceOrderPayLink.usrProcessingCenterID>(invoice);
			}
		}

		protected virtual void _(Events.FieldDefaulting<ServiceOrderPayLink.usrProcessingCenterID> e)
		{
			var row = e.Row as FSServiceOrder;

			var disablePayLink = false;
			var custClass = GetCustomerClass();
			if (custClass != null)
			{
				var custClassExt = GetCustomerClassExt(custClass);
				disablePayLink = custClassExt.DisablePayLink.GetValueOrDefault();
			}

			if (disablePayLink == false)
			{
				CCProcessingCenter procCenter = PXSelectJoin<CCProcessingCenter,
					InnerJoin<CashAccount, On<CCProcessingCenter.cashAccountID, Equal<CashAccount.cashAccountID>>,
					InnerJoin<CCProcessingCenterBranch, On<CCProcessingCenterBranch.processingCenterID, Equal<CCProcessingCenter.processingCenterID>>>>,
					Where<CashAccount.curyID, Equal<Required<CashAccount.curyID>>,
						And<CCProcessingCenter.allowPayLink, Equal<True>,
						And<CCProcessingCenter.isActive, Equal<True>,
						And<CCProcessingCenterBranch.defaultForBranch, Equal<True>,
						And<CCProcessingCenterBranch.branchID, Equal<Required<CCProcessingCenterBranch.branchID>>>>>>>>
					.Select(Base, row.CuryID, row.BranchID);

				if (procCenter != null)
				{
					e.NewValue = procCenter.ProcessingCenterID;
				}
			}

		}

		protected virtual PayLinkProcessingParams CollectDataToSyncLink(FSServiceOrder doc, CCPayLink payLink)
		{
			var payLinkData = new PayLinkProcessingParams();
			payLinkData.DueDate = doc.OrderDate.Value;
			payLinkData.LinkGuid = payLink.NoteID;
			payLinkData.ExternalId = payLink.ExternalID;

			CalculateAndSetLinkAmount(doc, payLinkData);

			return payLinkData;
		}

		protected virtual PayLinkProcessingParams CollectDataToCreateLink(FSServiceOrder doc)
        {
            var docExt = Base.ServiceOrderRecords.Cache.GetExtension<ServiceOrderPayLink>(doc);
            var payLinkData = new PayLinkProcessingParams();
            var procCenterStr = docExt.UsrProcessingCenterID;
            var pc = GetPaymentProcessingRepo().GetProcessingCenterByID(procCenterStr);
            var meansOfPayment = GetMeansOfPayment(PayLinkDocument.Current, GetCustomerClass());

            string customerPCID = GetCustomerProfileId(doc.CustomerID, docExt.UsrProcessingCenterID);
            if (customerPCID == null)
            {
                var payLinkProc = GetPayLinkProcessing();
                customerPCID = payLinkProc.CreateCustomerProfileId(doc.CustomerID, docExt.UsrProcessingCenterID);
            }

            payLinkData.MeansOfPayment = meansOfPayment;
			payLinkData.DueDate = doc.OrderDate.Value;
			payLinkData.CustomerProfileId = customerPCID;
			payLinkData.AllowPartialPayments = pc.AllowPartialPayment.GetValueOrDefault();
			payLinkData.FormTitle = CreateFormTitle(doc);

			CalculateAndSetLinkAmount(doc, payLinkData);

			return payLinkData;
        }

        private CustomerClass GetCustomerClass()
        {
			if (Base.TaxCustomer.Current != null)
			{
				return SelectFrom<CustomerClass>
					.Where<CustomerClass.customerClassID.IsEqual<@P.AsString>>.View.Select(Base,
					Base.TaxCustomer.Current.CustomerClassID);
			}
			else return null;
        }
        private CustomerClassPayLink GetCustomerClassExt(CustomerClass custClass)
        {
            return PXCache<CustomerClass>.GetExtension<CustomerClassPayLink>(custClass);
        }

        protected virtual void CalculateAndSetLinkAmount(FSServiceOrder doc, PayLinkProcessingParams payLinkParams)
		{
			var amountToSend = 0m;
			var docExt = PayLinkDocument.Current;
			var docData = new DocumentData();
			docData.DocType = doc.SrvOrdType;
			docData.DocRefNbr = doc.RefNbr;
			docData.DocBalance = doc.CuryEstimatedBillableTotal.Value;
			payLinkParams.DocumentData = docData;

			List<DocumentDetailData> detailData = new List<DocumentDetailData>();
			amountToSend += PopulateDocDetailData(detailData);
			docData.DocumentDetails = detailData;

			var aplDocData = new List<PX.CCProcessingBase.Interfaces.V2.AppliedDocumentData>();
			amountToSend -= PopulateAppliedDocData(aplDocData, docExt, payLinkParams);

			docData.AppliedDocuments = aplDocData;
			payLinkParams.Amount = amountToSend;
		}

		protected virtual string CreateFormTitle(FSServiceOrder invoice)
		{
			var cust = GetCustomer(invoice.CustomerID);
			var title = string.Empty;
			if (invoice.RefNbr != null)
			{
				title += invoice.RefNbr;
			}
			if (cust.AcctName != null)
			{
				title += " " + cust.AcctName;
			}
			if (invoice.DocDesc != null)
			{
				title += " " + invoice.DocDesc;
			}
			title = title.Trim();
			return title;
		}
		private void CreatePayments(FSServiceOrder serviceOrder, CCPayLink payLink, PayLinkData payLinkData,
			bool createStandalonePmt = false)
		{
			if (payLinkData.Transactions == null) return;

			PXException ex = null;
			var invoiceExt = PayLinkDocument.Current;
			var paymentGraph = PXGraph.CreateInstance<ARPaymentEntry>();
			var pc = GetPaymentProcessingRepo().GetProcessingCenterByID(invoiceExt.ProcessingCenterID);

			foreach (var tranData in payLinkData.Transactions.OrderBy(i => i.SubmitTime))
			{
				using (var scope = new PXTransactionScope())
				{
					if (!(tranData.TranType == CCTranType.AuthorizeAndCapture
						&& tranData.TranStatus == CCTranStatus.Approved))
					{
						continue;
					}
					try
					{
						TranValidationHelper.CheckTranAlreadyRecorded(tranData,
							new TranValidationHelper.AdditionalParams()
							{
								ProcessingCenter = pc.ProcessingCenterID,
								Repo = new CCPaymentProcessingRepository(Base)
							});
					}
					catch (TranValidationHelper.TranValidationException)
					{
						continue;
					}

					var res = GetMappingRow(invoiceExt.BranchID, invoiceExt.ProcessingCenterID);
					var mappingRow = res.Item2;

					try
					{
						CheckTranAgainstMapping(mappingRow, tranData);
					}
					catch (PXException checkMappingEx)
					{
						ex = checkMappingEx;
						continue;
					}

					string paymentMethodId = null;
					int? cashAccId = null;

					if (tranData.PaymentMethodType == PX.CCProcessingBase.Interfaces.V2.MeansOfPayment.CreditCard)
					{
						paymentMethodId = mappingRow.CCPaymentMethodID;
						cashAccId = mappingRow.CCCashAccountID;
					}
					else
					{
						paymentMethodId = mappingRow.EFTPaymentMethodID;
						cashAccId = mappingRow.EFTCashAccountID;
					}

					var pmtDate = PXTimeZoneInfo.ConvertTimeFromUtc(tranData.SubmitTime, LocaleInfo.GetTimeZone()).Date;

					ARPayment payment = new ARPayment();
					payment.AdjDate = pmtDate;
					payment.BranchID = serviceOrder.BranchID;
					payment.DocType = ARDocType.Prepayment;
					payment = paymentGraph.Document.Insert(payment);
					payment.CustomerID = serviceOrder.CustomerID;
					payment.CustomerLocationID = serviceOrder.LocationID;

					payment.PaymentMethodID = paymentMethodId;
					payment.CuryOrigDocAmt = tranData.Amount;
					payment.DocDesc = serviceOrder.DocDesc;
					payment = paymentGraph.Document.Update(payment);
					payment.PMInstanceID = PaymentTranExtConstants.NewPaymentProfile;
					payment.ProcessingCenterID = pc.ProcessingCenterID;
					payment.CashAccountID = cashAccId;
					payment.Hold = false;
					payment.DocDesc = PXMessages.LocalizeFormatNoPrefix(PX.Objects.CC.Messages.PayLinkPaymentDescr, payLink.DocType, payLink.RefNbr, payLink.ExternalID);
					payment = paymentGraph.Document.Update(payment);

					Base.InsertSOAdjustments(serviceOrder, null, paymentGraph, payment);
                    paymentGraph.Save.Press();

					var extension = paymentGraph.GetExtension<PX.Objects.AR.GraphExtensions.ARPaymentEntryPaymentTransaction>();
					CCPaymentEntry entry = new CCPaymentEntry(paymentGraph);
					extension.RecordTransaction(paymentGraph.Document.Current, tranData, entry);

					paymentGraph.Clear();
					scope.Complete();
				}
			}

			if (ex != null)
			{
				throw ex;
			}
		}

		private decimal PopulateAppliedDocData(List<AppliedDocumentData> aplDocData, PayLinkDocument payLinkDoc, PayLinkProcessingParams payLinkParams)
		{
			var adjDocTotal = 0m;
			var newLink = payLinkParams.ExternalId == null;
			var adjustments = Base.Adjustments.Select().RowCast<ARAdjust2>().Where(i => i.Released == true
				&& i.Voided == false);

			IEnumerable<ExternalTransaction> payLinkExtTran = null;

			if (payLinkDoc.PayLinkID != null && !newLink)
			{
				payLinkExtTran = GetPaymentProcessingRepo().GetExternalTransactionsByPayLinkID(payLinkDoc.PayLinkID);
			}

			foreach (var detail in adjustments)
			{
				bool adjHasRelatedPayLink = false;
				if (payLinkExtTran != null)
				{
					var res = payLinkExtTran.Any(i => i.DocType == detail.AdjgDocType && i.RefNbr == detail.AdjgRefNbr);
					if (res)
					{
						adjHasRelatedPayLink = true;
					}
				}

				var aplDocDataItem = new AppliedDocumentData();

				decimal amtToSend = detail.CuryAdjdDiscAmt.Value + detail.CuryAdjdWOAmt.Value;
				if (!adjHasRelatedPayLink)
				{
					amtToSend += detail.CuryAdjdAmt.Value;
				}

				aplDocDataItem.Amount = amtToSend;
				aplDocDataItem.DocRefNbr = detail.AdjgRefNbr;
				aplDocDataItem.DocType = detail.AdjgDocType;
				adjDocTotal += amtToSend;
				aplDocData.Add(aplDocDataItem);
			}

			return adjDocTotal;
		}

		protected override void SaveDoc()
		{
			Base.Save.Press();
		}

		public override void SetCurrentDocument(FSServiceOrder doc)
		{
			Base.ServiceOrderRecords.Current = Base.ServiceOrderRecords.Search<ARInvoice.refNbr>(doc.RefNbr, doc.SrvOrdType);
		}

		protected virtual PayLinkDocumentMapping GetMapping()
		{
			return new PayLinkDocumentMapping(typeof(FSServiceOrder))
			{
				ProcessingCenterID = typeof(ServiceOrderPayLink.usrProcessingCenterID),
				DeliveryMethod = typeof(ServiceOrderPayLink.usrDeliveryMethod),
				DocType = typeof(FSServiceOrder.srvOrdType),
				RefNbr = typeof(FSServiceOrder.refNbr),
				BranchID = typeof(FSServiceOrder.branchID),
				PayLinkID = typeof(ServiceOrderPayLink.usrPayLinkID),
			};
		}

		private decimal PopulateDocDetailData(List<DocumentDetailData> detailData)
		{
			decimal total = 0;
			foreach (var detail in Base.ServiceOrderDetails.Select().RowCast<FSSODet>())
			{
				var detailDataItem = new DocumentDetailData();
				detailDataItem.ItemName = detail.TranDesc;
				detailDataItem.Price = detail.CuryEstimatedTranAmt.GetValueOrDefault();
				detailDataItem.Quantity = detail.Qty.Value;
				detailDataItem.Uom = detail.UOM;
				detailDataItem.LineNbr = detail.LineNbr.Value;

				total += detailDataItem.Price;

				detailData.Add(detailDataItem);
			}
			return total;
		}

		protected class PayLinkDocumentMapping : IBqlMapping
		{
			public Type DocType = typeof(PayLinkDocument.docType);
			public Type RefNbr = typeof(PayLinkDocument.refNbr);
			public Type BranchID = typeof(PayLinkDocument.branchID);
			public Type ProcessingCenterID = typeof(PayLinkDocument.processingCenterID);
			public Type DeliveryMethod = typeof(PayLinkDocument.deliveryMethod);
			public Type PayLinkID = typeof(PayLinkDocument.payLinkID);

			public Type Table { get; private set; }
			public Type Extension => typeof(PayLinkDocument);
			public PayLinkDocumentMapping(Type table)
			{
				Table = table;
			}
		}
	}
}