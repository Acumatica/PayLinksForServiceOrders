using System;

namespace PaymentLinksForServiceOrder
{
	public class ServiceOrderEntryPayLink : PayLinkDocumentGraph<ARInvoiceEntry, ARInvoice>
	{
		public static bool IsActive()
		{
			return PXAccess.FeatureInstalled<FeaturesSet.acumaticaPayments>();
		}

		public PXSelect<CCPayLink, Where<CCPayLink.payLinkID, Equal<Current<ARInvoicePayLink.payLinkID>>>> PayLink;

		[PXUIField(DisplayName = "Create Payment Link", Visible = true)]
		[PXButton(CommitChanges = true)]
		public override IEnumerable CreateLink(PXAdapter adapter)
		{
			SaveDoc();
			var docs = adapter.Get<ARInvoice>().ToList();

			PXLongOperation.StartOperation(Base, delegate
			{
				foreach (var doc in docs)
				{
					var graph = PXGraph.CreateInstance<ARInvoiceEntry>();
					var ext = graph.GetExtension<ARInvoiceEntryPayLink>();
					var processSoLinks = ext.ProcessPayLinkFromRelatedOrders(doc);
					graph.Document.Cache.Clear();
					ARInvoice invoice = graph.Document.Search<ARInvoice.refNbr>(doc.RefNbr, doc.DocType);

					if (processSoLinks && invoice.OpenDoc == false) return;

					graph.Document.Current = invoice;
					ext.CollectDataAndCreateLink();
				}
			});
			return docs;
		}

		[PXOverride]
		public virtual void ARInvoiceCreated(ARInvoice invoice, ARRegister doc, Action<ARInvoice, ARRegister> baseMethod)
		{
			baseMethod(invoice, doc);
			if (invoice.DocType != ARDocType.Invoice)
			{
				var cache = Base.Document.Cache;
				cache.SetValueExt<ARInvoicePayLink.deliveryMethod>(invoice, null);
				cache.SetValueExt<ARInvoicePayLink.processingCenterID>(invoice, null);
				cache.SetValueExt<ARInvoicePayLink.payLinkID>(invoice, null);
			}
		}

		[PXOverride]
		public virtual void Persist(Action baseMethod)
		{
			var curDoc = Base.Document.Current;
			if (curDoc != null && curDoc.DocType == ARDocType.Invoice)
			{
				var payLink = PayLink.SelectSingle();

				if (payLink != null && CheckPayLinkRelatedToDoc(payLink)
					&& payLink.NeedSync == false && PayLinkHelper.PayLinkOpen(payLink))
				{
					var needSync = payLink.Amount != curDoc.CuryDocBal
						|| payLink.DueDate != curDoc.DueDate.Value;

					var origCuryDocBal = Base.Document.Cache
						.GetValueOriginal<ARInvoice.curyDocBal>(curDoc) as decimal?;
					var origDueDate = Base.Document.Cache
						.GetValueOriginal<ARInvoice.dueDate>(curDoc) as DateTime?;

					needSync = needSync && (curDoc.CuryDocBal != origCuryDocBal
						|| curDoc.DueDate != origDueDate);

					if (needSync)
					{
						payLink.NeedSync = needSync;
						PayLink.Update(payLink);
					}
				}
			}
			baseMethod();
		}

		public virtual void UpdatePayLinkAndCreatePayments(V2.PayLinkData payLinkData)
		{
			var link = PayLink.SelectSingle();
			var doc = Base.Document.Current;
			var payLinkProcessing = GetPayLinkProcessing();
			var copyDoc = Base.Document.Cache.CreateCopy(doc) as ARInvoice;
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
			var doc = Base.Document.Current;
			var copyDoc = Base.Document.Cache.CreateCopy(doc) as ARInvoice;
			var payLinkDoc = PayLinkDocument.Current;
			var link = PayLink.SelectSingle();
			var payLinkData = payLinkProcessing.GetPayments(payLinkDoc, link);
			if (payLinkData.Transactions != null && payLinkData.Transactions.Any())
			{
				try
				{
					ARReleaseProcessPayLink.ActivateDoNotSyncFlag();
					CreatePayments(copyDoc, link, payLinkData);
				}
				catch (Exception ex)
				{
					payLinkProcessing.SetErrorStatus(ex, link);
					throw;
				}
				finally
				{
					ARReleaseProcessPayLink.ClearDoNotSyncFlag();
				}
			}
			
			payLinkProcessing.SetLinkStatus(link, payLinkData);
	
			copyDoc = GetInvoiceFromDB(copyDoc);

			if ((copyDoc.OpenDoc == false || copyDoc.CuryDocBal == 0)
				&& payLinkData.StatusCode == V2.PayLinkStatus.Open)
			{
				var closeParams = new V2.PayLinkProcessingParams();
				closeParams.LinkGuid = link.NoteID;
				closeParams.ExternalId = link.ExternalID;
				payLinkProcessing.CloseLink(payLinkDoc, link, closeParams);
				return;
			}

			if (link.NeedSync == false || payLinkData.StatusCode == V2.PayLinkStatus.Closed
				 || payLinkData.PaymentStatusCode == V2.PayLinkPaymentStatus.Paid)
			{
				return;
			}

			var data = CollectDataToSyncLink(copyDoc, link);
			payLinkProcessing.SyncLink(payLinkDoc, link, data);
		}

		public virtual bool ProcessPayLinkFromRelatedOrders(ARInvoice inv)
		{
			bool ret = false;
			var payLinkProcessing = GetPayLinkProcessing();

			foreach (var row in GetPayLinkOrdersRelatedToInvoice(inv))
			{
				CCPayLink payLink = row.Item1;
				SOOrder order = row.Item2;
				SOOrderPayLink orderPayLink =  Base.Caches[typeof(SOOrder)].GetExtension<SOOrderPayLink>(order);

				var processed = PayLinkHelper.PayLinkWasProcessed(payLink);
				if (processed || payLink.ExternalID == null) continue;

				var doc = new PayLinkDocument();
				doc.OrderType = payLink.OrderType;
				doc.OrderNbr = payLink.OrderNbr;
				doc.DeliveryMethod = payLink.DeliveryMethod;
				doc.ProcessingCenterID = orderPayLink.ProcessingCenterID;

				var payLinkData = payLinkProcessing.GetPayments(doc, payLink);
				if (payLinkData.Transactions != null && payLinkData.Transactions.Any())
				{
					try
					{
						ret = true;
						CreatePayments(order, payLink, payLinkData);
					}
					catch (Exception ex)
					{
						payLinkProcessing.SetErrorStatus(ex, payLink);
						throw;
					}
				}

				payLinkProcessing.SetLinkStatus(payLink, payLinkData);

				if (payLinkData.PaymentStatusCode == V2.PayLinkPaymentStatus.PartiallyPaid
					|| payLinkData.PaymentStatusCode == V2.PayLinkPaymentStatus.Unpaid)
				{
					var data = new V2.PayLinkProcessingParams();
					data.LinkGuid = payLink.NoteID;
					data.ExternalId = payLink.ExternalID;
					payLinkProcessing.CloseLink(doc, payLink, data);
				}
			}
			return ret;
		}

		public override void CollectDataAndCreateLink()
		{
			var doc = Base.Document.Current;
			CheckDocBeforeLinkCreation(doc);
			var data = CollectDataToCreateLink(doc);
			var payLinkProcessing = GetPayLinkProcessing();
			var payLinkDoc = PayLinkDocument.Current;

			CCPayLink payLink;
			using (var scope = new PXTransactionScope())
			{
				payLink = payLinkProcessing.CreateLinkInDB(payLinkDoc, data);
				PayLinkDocument.Update(payLinkDoc);
				Base.Save.Press();
				scope.Complete();
			}

			payLinkProcessing.SendCreateLinkRequest(payLink, data);

			if (payLinkDoc.DeliveryMethod == PayLinkDeliveryMethod.Email)
			{
				SendNotification();
			}
		}

		public void CreateStandalonePayments(ARInvoice invoice, CCPayLink payLink, V2.PayLinkData payLinkData)
		{
			CreatePayments(invoice, payLink, payLinkData, true);
		}

		public virtual void CreatePayments(ARInvoice invoice, CCPayLink payLink, V2.PayLinkData payLinkData)
		{
			CreatePayments(invoice, payLink, payLinkData, false);
		}

		public override void SendNotification()
		{
			const string id = "INVOICE PAY LINK";

			var payLinkDoc = PayLinkDocument.Current;
			if (payLinkDoc.DeliveryMethod != PayLinkDeliveryMethod.Email) return;

			var invoice = Base.Document.Current;
			var prms = new Dictionary<string, string>
			{
				["DocType"] = invoice.DocType,
				["RefNbr"] = invoice.RefNbr
			};
			var activityExt = Base.GetExtension<ARInvoiceEntry_ActivityDetailsExt>();
			activityExt.SendNotification(ARNotificationSource.Customer, id, invoice.BranchID, prms, true);
		}

		protected virtual void _(Events.RowSelected<ARInvoice> e)
		{
			var doc = e.Row;
			var cache = e.Cache;
			if (doc == null) return;

			var disablePayLink = false;
			var allowOverrideDeliveryMethod = false;
			var custClass = Base.customerclass.SelectSingle();
			if (custClass != null)
			{
				var custClassExt = GetCustomerClassExt(custClass);
				disablePayLink = custClassExt.DisablePayLink.GetValueOrDefault();
				allowOverrideDeliveryMethod = custClassExt.AllowOverrideDeliveryMethod.GetValueOrDefault();
			}

			var docExt = PayLinkDocument.Current;
			var link = PayLink.SelectSingle();
			var isInvocie = doc.DocType == ARDocType.Invoice;
			var released = doc.Released == true;
			var needNewLink = link?.Url == null || PayLinkHelper.PayLinkWasProcessed(link);
			var closedAndUnpaid = link != null && PayLinkHelper.PayLinkWasProcessed(link)
				&& link.PaymentStatus == PayLinkPaymentStatus.Unpaid;

			createLink.SetEnabled(isInvocie && released && doc.OpenDoc == true
				&& docExt.ProcessingCenterID != null && needNewLink && !disablePayLink);
			createLink.SetVisible(!disablePayLink && isInvocie);
			syncLink.SetEnabled(isInvocie && released && !needNewLink
				&& !disablePayLink && docExt.ProcessingCenterID != null);
			syncLink.SetVisible(!disablePayLink && isInvocie);
			resendLink.SetEnabled(isInvocie && released && !needNewLink
				&& docExt.ProcessingCenterID != null && !disablePayLink && docExt.DeliveryMethod == PayLinkDeliveryMethod.Email);
			resendLink.SetVisible(!disablePayLink && isInvocie);

			var hidePayLinkFields = disablePayLink && PayLink.SelectSingle()?.Url == null;
			var payLinkControlsVisible = isInvocie && !hidePayLinkFields;
			var payLinkControlsEnabled = payLinkControlsVisible && (link?.Url == null || closedAndUnpaid);

			PXUIFieldAttribute.SetEnabled<ARInvoicePayLink.processingCenterID>(cache, doc, payLinkControlsEnabled);
			PXUIFieldAttribute.SetEnabled<ARInvoicePayLink.deliveryMethod>(cache, doc, payLinkControlsEnabled
				&& allowOverrideDeliveryMethod);

			PXUIFieldAttribute.SetVisible<ARInvoicePayLink.processingCenterID>(cache, doc, payLinkControlsVisible);
			PXUIFieldAttribute.SetVisible<ARInvoicePayLink.deliveryMethod>(cache, doc, payLinkControlsVisible);
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

		protected virtual void _(Events.FieldDefaulting<ARInvoicePayLink.deliveryMethod> e)
		{
			var row = e.Row as ARInvoice;
			if (row?.DocType == ARDocType.Invoice)
			{
				var custClass = Base.customerclass.SelectSingle();
				if (custClass == null) return;

				var custClassExt = GetCustomerClassExt(custClass);
				if (custClassExt.DisablePayLink.GetValueOrDefault() == false)
				{
					e.NewValue = custClassExt.DeliveryMethod;
				}
			}
		}

		protected virtual void _(Events.FieldUpdated<ARInvoice.curyID> e)
		{
			var invoice = (ARInvoice)e.Row;
			var cache = e.Cache;
			if (invoice == null) return;

			cache.SetDefaultExt<ARInvoicePayLink.processingCenterID>(invoice);
		}

		protected virtual void _(Events.FieldUpdated<ARInvoice, ARInvoice.branchID> e)
		{
			var invoice = e.Row;
			var cache = e.Cache;
			var newVal = e.NewValue as int?;
			var oldVal = e.OldValue as int?;
			if (invoice == null) return;
			if (newVal == oldVal) return;

			var payLink = PayLink.SelectSingle();
			if (payLink?.Url != null && !PayLinkHelper.PayLinkWasProcessed(payLink)) return;

			cache.SetDefaultExt<ARInvoicePayLink.processingCenterID>(invoice);
		}

		protected virtual void _(Events.FieldUpdated<ARInvoice.customerID> e)
		{
			var invoice = (ARInvoice)e.Row;
			var cache = e.Cache;
			if (invoice != null)
			{
				cache.SetDefaultExt<ARInvoicePayLink.deliveryMethod>(invoice);
				cache.SetDefaultExt<ARInvoicePayLink.processingCenterID>(invoice);	
			}
		}

		protected virtual void _(Events.FieldDefaulting<ARInvoicePayLink.processingCenterID> e)
		{
			var row = e.Row as ARInvoice;

			if (row?.DocType == ARDocType.Invoice)
			{
				var disablePayLink = false;
				var custClass = Base.customerclass.SelectSingle();
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
		}

		protected virtual IEnumerable<Tuple<CCPayLink, SOOrder>> GetPayLinkOrdersRelatedToInvoice(ARInvoice inv)
		{
			foreach (PXResult<SOOrder, CCPayLink, SOOrderShipment, ARRegister> row in PXSelectJoin<SOOrder,
				InnerJoin<CCPayLink, On<CCPayLink.payLinkID, Equal<SOOrderPayLink.payLinkID>>,
				InnerJoin<SOOrderShipment, On<SOOrderShipment.orderType, Equal<SOOrder.orderType>,
					And<SOOrderShipment.orderNbr, Equal<SOOrder.orderNbr>>>,
				InnerJoin<ARRegister, On<ARRegister.docType, Equal<SOOrderShipment.invoiceType>,
					And<ARRegister.refNbr, Equal<SOOrderShipment.invoiceNbr>>>>>>,
				Where<SOOrderShipment.invoiceType, Equal<Required<SOOrderShipment.invoiceType>>,
					And<SOOrderShipment.invoiceNbr, Equal<Required<SOOrderShipment.invoiceNbr>>>>,
				OrderBy<Asc<ARRegister.createdDateTime>>>.Select(Base, inv.DocType, inv.RefNbr))
			{
				CCPayLink payLink = row;
				SOOrder order = row;
				yield return new Tuple<CCPayLink, SOOrder>(payLink, order);
			}
		}

		protected virtual V2.PayLinkProcessingParams CollectDataToSyncLink(ARInvoice doc, CCPayLink payLink)
		{
			var payLinkData = new V2.PayLinkProcessingParams();
			payLinkData.DueDate = doc.DueDate.Value;
			payLinkData.LinkGuid = payLink.NoteID;
			payLinkData.ExternalId = payLink.ExternalID;

			CalculateAndSetLinkAmount(doc, payLinkData);

			return payLinkData;
		}

		protected virtual V2.PayLinkProcessingParams CollectDataToCreateLink(ARInvoice doc)
		{
			var docExt = Base.Document.Cache.GetExtension<ARInvoicePayLink>(doc);
			var payLinkData = new V2.PayLinkProcessingParams();
			var procCenterStr = docExt.ProcessingCenterID;
			var pc = GetPaymentProcessingRepo().GetProcessingCenterByID(procCenterStr);
			var meansOfPayment = GetMeansOfPayment(PayLinkDocument.Current, Base.customerclass.SelectSingle());

			string customerPCID = GetCustomerProfileId(doc.CustomerID, docExt.ProcessingCenterID);
			if (customerPCID == null)
			{
				var payLinkProc = GetPayLinkProcessing();
				customerPCID = payLinkProc.CreateCustomerProfileId(doc.CustomerID, docExt.ProcessingCenterID);
			}

			payLinkData.MeansOfPayment = meansOfPayment;
			payLinkData.DueDate = doc.DueDate.Value;
			payLinkData.CustomerProfileId = customerPCID;
			payLinkData.AllowPartialPayments = pc.AllowPartialPayment.GetValueOrDefault();
			payLinkData.FormTitle = CreateFormTitle(doc);

			CalculateAndSetLinkAmount(doc, payLinkData);

			return payLinkData;
		}

		protected virtual void CalculateAndSetLinkAmount(ARInvoice doc, V2.PayLinkProcessingParams payLinkParams)
		{
			var amountToSend = 0m;
			var docExt = PayLinkDocument.Current;
			var docData = new V2.DocumentData();
			docData.DocType = doc.DocType;
			docData.DocRefNbr = doc.RefNbr;
			docData.DocBalance = doc.CuryDocBal.Value;
			payLinkParams.DocumentData = docData;

			List<V2.DocumentDetailData> detailData = new List<V2.DocumentDetailData>();
			amountToSend += PopulateDocDetailData(detailData);
			docData.DocumentDetails = detailData;

			var headerTaxes = CalculateHeaderTaxes(doc);
			if (headerTaxes > 0)
			{
				docData.ExcludedTaxes = headerTaxes.Value;
				amountToSend += docData.ExcludedTaxes;
			}

			var docDiscounts = CalculateDocDiscounts(doc);
			if (docDiscounts != 0)
			{
				docData.DocDiscounts = docDiscounts.Value;
				amountToSend += (-1 * docData.DocDiscounts);
			}

			if (doc.CuryFreightTot != 0)
			{
				docData.Freight = doc.CuryFreightTot.Value;
				amountToSend += docData.Freight;
			}

			var aplDocData = new List<V2.AppliedDocumentData>();
			amountToSend -= PopulateAppliedDocData(aplDocData, docExt, payLinkParams);

			docData.AppliedDocuments = aplDocData;
			payLinkParams.Amount = amountToSend;
		}

		protected virtual string CreateFormTitle(ARInvoice invoice)
		{
			var cust = GetCustomer(invoice.CustomerID);
			var title = string.Empty;
			if (invoice.InvoiceNbr != null)
			{
				title += invoice.InvoiceNbr;
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
		private void CreatePayments(ARInvoice invoice, CCPayLink payLink, V2.PayLinkData payLinkData,
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
					if (!(tranData.TranType == V2.CCTranType.AuthorizeAndCapture
						&& tranData.TranStatus == V2.CCTranStatus.Approved))
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

					if (tranData.PaymentMethodType == V2.MeansOfPayment.CreditCard)
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
					payment.BranchID = invoice.BranchID;
					payment.DocType = ARDocType.Payment;
					payment = paymentGraph.Document.Insert(payment);
					payment.CustomerID = invoice.CustomerID;
					payment.CustomerLocationID = invoice.CustomerLocationID;
					payment.ARAccountID = invoice.ARAccountID;
					payment.ARSubID = invoice.ARSubID;
					payment.PaymentMethodID = paymentMethodId;
					payment.CuryOrigDocAmt = tranData.Amount;
					payment.DocDesc = invoice.DocDesc;
					payment = paymentGraph.Document.Update(payment);
					payment.PMInstanceID = PaymentTranExtConstants.NewPaymentProfile;
					payment.ProcessingCenterID = pc.ProcessingCenterID;
					payment.CashAccountID = cashAccId;
					payment.Hold = false;
					payment.DocDesc = PXMessages.LocalizeFormatNoPrefix(Messages.PayLinkPaymentDescr, payLink.DocType, payLink.RefNbr, payLink.ExternalID);
					payment = paymentGraph.Document.Update(payment);
					paymentGraph.Save.Press();

					if (!createStandalonePmt && invoice.CuryDocBal != 0)
					{
						if (invoice.PaymentsByLinesAllowed == true)
						{
							foreach (ARTran tran in PXSelect<ARTran, Where<ARTran.tranType, Equal<Required<ARInvoice.docType>>,
														And<ARTran.refNbr, Equal<Required<ARInvoice.refNbr>>,
														And<ARTran.curyTranBal, NotEqual<Zero>>>>,
														OrderBy<Desc<ARTran.curyTranBal>>>.Select(Base, invoice.DocType, invoice.RefNbr))
							{
								ARAdjust adj = new ARAdjust();
								adj.AdjdDocType = invoice.DocType;
								adj.AdjdRefNbr = invoice.RefNbr;
								adj.AdjdLineNbr = tran.LineNbr;
								paymentGraph.Adjustments.Insert(adj);

								if (payment.CuryApplAmt >= tranData.Amount) break;
							}
						}
						else
						{
							ARAdjust adj = new ARAdjust();
							adj.AdjdDocType = invoice.DocType;
							adj.AdjdRefNbr = invoice.RefNbr;
							paymentGraph.Adjustments.Insert(adj);
						}
						paymentGraph.Save.Press();
					}

					var extension = paymentGraph.GetExtension<AR.GraphExtensions.ARPaymentEntryPaymentTransaction>();
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

		private void CheckDocBeforeLinkCreation(ARInvoice doc)
		{
			if (doc.OpenDoc == false)
			{
				throw new PXException(Messages.CannotCreateLinkForDocument, doc.DocType, doc.RefNbr);
			}
		}

		private decimal PopulateAppliedDocData(List<V2.AppliedDocumentData> aplDocData, PayLinkDocument payLinkDoc, V2.PayLinkProcessingParams payLinkParams)
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

				var aplDocDataItem = new V2.AppliedDocumentData();

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

		public override void SetCurrentDocument(ARInvoice doc)
		{
			Base.Document.Current = Base.Document.Search<ARInvoice.refNbr>(doc.RefNbr, doc.DocType);
		}

		protected virtual ARInvoice GetInvoiceFromDB(ARInvoice doc)
		{
			ARInvoice fromDb = PXSelectReadonly<ARInvoice, Where<ARInvoice.docType, Equal<Required<ARInvoice.docType>>,
				And<ARInvoice.refNbr, Equal<Required<ARInvoice.refNbr>>>>>
				.Select(Base, doc.DocType, doc.RefNbr);
			return fromDb;
		}

		protected virtual PayLinkDocumentMapping GetMapping()
		{
			return new PayLinkDocumentMapping(typeof(ARInvoice));
		}

		private decimal PopulateDocDetailData(List<V2.DocumentDetailData> detailData)
		{
			decimal total = 0;
			foreach (var detail in Base.Transactions.Select().RowCast<ARTran>()
				.Where(i => i.LineType.IsNotIn(SOLineType.Discount, SOLineType.Freight)))
			{
				var detailDataItem = new V2.DocumentDetailData();
				detailDataItem.ItemName = detail.TranDesc;
				detailDataItem.Price = detail.CuryTranAmt.GetValueOrDefault();
				detailDataItem.Quantity = detail.Qty.Value;
				detailDataItem.Uom = detail.UOM;
				detailDataItem.LineNbr = detail.LineNbr.Value;

				total += detailDataItem.Price;

				detailData.Add(detailDataItem);
			}
			return total;
		}

		private decimal? CalculateDocDiscounts(ARInvoice invoice)
		{
			var docDiscounts = 0m;
			var discDetails = Base.ARDiscountDetails.Select().RowCast<ARInvoiceDiscountDetail>();
			foreach (var discItem in discDetails.Where(i => i.SkipDiscount == false))
			{
				docDiscounts += discItem.CuryDiscountAmt.GetValueOrDefault();
			}
			return docDiscounts;
		}

		private decimal? CalculateHeaderTaxes(ARInvoice invoice)
		{
			var headerTaxes = invoice.CuryTaxTotal;
			var taxCalcMode = invoice.TaxCalcMode;

			foreach (var taxItem in GetTaxes(invoice))
			{
				var taxRule = taxItem.Item2.TaxCalcRule;
				var taxAmt = taxItem.Item1.CuryTaxAmt.GetValueOrDefault();
				var compoundLineLevel = CSTaxCalcType.Item + CSTaxCalcLevel.CalcOnItemAmtPlusTaxAmt;
				if (taxCalcMode == TaxCalculationMode.Gross && taxRule != compoundLineLevel)
				{
					headerTaxes -= taxAmt;
				}
				else if (taxItem.Item2.TaxCalcLevel == "0" && taxCalcMode != TaxCalculationMode.Net)
				{
					headerTaxes -= taxAmt;
				}
			}
			return headerTaxes;
		}

		private List<Tuple<ARTaxTran, Tax>> GetTaxes(ARInvoice invoice)
		{
			var res = PXSelectJoin<ARTaxTran, InnerJoin<Tax, On<ARTaxTran.taxID, Equal<Tax.taxID>>>,
				Where<ARTaxTran.refNbr, Equal<Required<ARTaxTran.refNbr>>,
					And<ARTaxTran.tranType, Equal<Required<ARTaxTran.tranType>>>>>
			.Select(Base, invoice.RefNbr, invoice.DocType);

			var taxList = new List<Tuple<ARTaxTran, Tax>>();
			foreach (PXResult<ARTaxTran, Tax> item in res)
			{
				taxList.Add(Tuple.Create((ARTaxTran)item, (Tax)item));
			}
			return taxList;
		}

		private CustomerClassPayLink GetCustomerClassExt(CustomerClass custClass)
		{
			return Base.customerclass.Cache.GetExtension<CustomerClassPayLink>(custClass);
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
	{

	}
}